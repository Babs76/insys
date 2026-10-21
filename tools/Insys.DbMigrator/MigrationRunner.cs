using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Npgsql;

namespace Insys.DbMigrator;

public sealed record Migration(int Version, string Name, string Path, string Checksum);

public sealed record MigrationResult(IReadOnlyList<Migration> Applied, IReadOnlyList<Migration> AlreadyApplied);

/// <summary>
/// Applies db/migrations/V####__name.sql in order, each in its own transaction, and records them in
/// public.schema_migration with a SHA-256 checksum. Migrations are forward-only (DBA-017): the runner refuses to
/// continue if an applied file has changed, or if a new file has a lower version than one already applied.
/// A PostgreSQL advisory lock stops two runners applying migrations at the same time.
/// </summary>
public sealed partial class MigrationRunner(string connectionString, Action<string>? log = null)
{
    private const long AdvisoryLockKey = 0x494E_5359_534D_4947; // "INSYSMIG"

    [GeneratedRegex(@"^V(?<version>\d{4})__(?<name>[a-z0-9_]+)\.sql$")]
    private static partial Regex FileNamePattern();

    [GeneratedRegex(@"^[a-z_][a-z0-9_]*$")]
    private static partial Regex RoleNamePattern();

    public static IReadOnlyList<Migration> Discover(string directory)
    {
        var migrations = new List<Migration>();
        foreach (var path in Directory.GetFiles(directory, "*.sql"))
        {
            var match = FileNamePattern().Match(System.IO.Path.GetFileName(path));
            if (!match.Success)
            {
                throw new InvalidOperationException($"Migration file name '{System.IO.Path.GetFileName(path)}' does not match V####__name.sql.");
            }

            var checksum = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
            migrations.Add(new Migration(int.Parse(match.Groups["version"].Value), match.Groups["name"].Value, path, checksum));
        }

        var duplicate = migrations.GroupBy(m => m.Version).FirstOrDefault(g => g.Count() > 1);
        return duplicate is not null
            ? throw new InvalidOperationException($"More than one migration has version {duplicate.Key}.")
            : migrations.OrderBy(m => m.Version).ToList();
    }

    public async Task<MigrationResult> Migrate(string directory, CancellationToken cancellationToken = default)
    {
        var migrations = Discover(directory);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await Execute(connection, "SELECT pg_advisory_lock(@key)", cancellationToken, ("key", AdvisoryLockKey));
        try
        {
            await Execute(connection, """
                CREATE TABLE IF NOT EXISTS public.schema_migration (
                  version      integer PRIMARY KEY,
                  name         varchar(200) NOT NULL,
                  checksum     char(64) NOT NULL,
                  applied_at   timestamptz NOT NULL DEFAULT now(),
                  applied_by   varchar(80) NOT NULL DEFAULT current_user,
                  execution_ms integer NOT NULL
                )
                """, cancellationToken);

            var applied = new Dictionary<int, string>();
            await using (var select = new NpgsqlCommand("SELECT version, checksum FROM public.schema_migration", connection))
            await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    applied[reader.GetInt32(0)] = reader.GetString(1);
                }
            }

            var highestApplied = applied.Count == 0 ? 0 : applied.Keys.Max();
            var alreadyApplied = new List<Migration>();
            var newlyApplied = new List<Migration>();
            foreach (var migration in migrations)
            {
                if (applied.TryGetValue(migration.Version, out var checksum))
                {
                    if (!string.Equals(checksum, migration.Checksum, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Migration V{migration.Version:0000} has changed since it was applied. Applied migrations are never edited (DBA-017); add a new migration instead.");
                    }

                    alreadyApplied.Add(migration);
                    continue;
                }

                if (migration.Version < highestApplied)
                {
                    throw new InvalidOperationException(
                        $"Migration V{migration.Version:0000} is older than the latest applied migration V{highestApplied:0000}. Give it a higher version.");
                }

                await Apply(connection, migration, cancellationToken);
                newlyApplied.Add(migration);
            }

            return new MigrationResult(newlyApplied, alreadyApplied);
        }
        finally
        {
            await Execute(connection, "SELECT pg_advisory_unlock(@key)", CancellationToken.None, ("key", AdvisoryLockKey));
        }
    }

    /// <summary>Runs db/roles/app_role_grants.sql for a non-owner application role, creating the role if needed.</summary>
    public async Task GrantAppRole(string grantsFile, string role, string? password, CancellationToken cancellationToken = default)
    {
        if (!RoleNamePattern().IsMatch(role))
        {
            throw new ArgumentException($"'{role}' is not a valid role name.", nameof(role));
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var exists = await Scalar(connection, "SELECT count(*) FROM pg_roles WHERE rolname = @role", cancellationToken, ("role", role));
        if (Convert.ToInt64(exists) == 0)
        {
            if (password is null)
            {
                throw new InvalidOperationException($"Role {role} does not exist; set INSYS_APP_ROLE_PASSWORD to create it.");
            }

            // CREATE ROLE does not accept parameters; the password is quoted as a literal.
            var literal = "'" + password.Replace("'", "''", StringComparison.Ordinal) + "'";
            await Execute(connection, $"CREATE ROLE {role} LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE PASSWORD {literal}", cancellationToken);
        }

        var sql = (await File.ReadAllTextAsync(grantsFile, cancellationToken)).Replace("{{APP_ROLE}}", role, StringComparison.Ordinal);
        await Execute(connection, sql, cancellationToken);
        log?.Invoke($"Granted application privileges to {role}.");
    }

    private async Task Apply(NpgsqlConnection connection, Migration migration, CancellationToken cancellationToken)
    {
        var sql = await File.ReadAllTextAsync(migration.Path, cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 0 })
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var record = new NpgsqlCommand(
            "INSERT INTO public.schema_migration (version, name, checksum, execution_ms) VALUES (@version, @name, @checksum, @ms)", connection, transaction))
        {
            record.Parameters.AddWithValue("version", migration.Version);
            record.Parameters.AddWithValue("name", migration.Name);
            record.Parameters.AddWithValue("checksum", migration.Checksum);
            record.Parameters.AddWithValue("ms", (int)stopwatch.ElapsedMilliseconds);
            await record.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        log?.Invoke($"Applied V{migration.Version:0000}__{migration.Name} in {stopwatch.ElapsedMilliseconds} ms.");
    }

    private static async Task Execute(NpgsqlConnection connection, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 0 };
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<object?> Scalar(NpgsqlConnection connection, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteScalarAsync(cancellationToken);
    }
}
