/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : DbMigrator
 * CONFIDENTIALITY AND INTELLECTUAL PROPERTY NOTICE
 * This source code and its associated documentation contain confidential and
 * proprietary information belonging to OpenFactor Technology Group or its
 * applicable intellectual property rights holder.
 * This material is provided solely for authorised development, maintenance,
 * operation or evaluation purposes, as applicable. No person may reproduce,
 * modify, distribute, disclose, publish or otherwise use this material except
 * as expressly permitted by an applicable written agreement or licence.
 * No ownership rights or licence to any intellectual property are granted
 * by the inclusion of this notice, except as expressly provided in an
 * applicable written agreement or licence.
 * Third-party software, open-source components and customer-owned materials
 * remain subject to their respective ownership rights and applicable licences
 * or agreements.
 * UNAUTHORISED ACCESS, USE, COPYING OR DISCLOSURE IS PROHIBITED.
 * ============================================================================
 */
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Npgsql;

namespace Insys.DbMigrator;

public sealed record Migration(int Version, string Name, string Path, string Checksum);

public sealed record MigrationResult(IReadOnlyList<Migration> Applied, IReadOnlyList<Migration> AlreadyApplied, IReadOnlyList<string>? RepeatablesApplied = null);

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
    private static partial Regex VersionedPattern();

    /// <summary>Repeatable scripts (seed data generated from code) run after the versioned ones whenever their content changes.</summary>
    [GeneratedRegex(@"^R__(?<name>[a-z0-9_]+)\.sql$")]
    private static partial Regex RepeatablePattern();

    public static IReadOnlyList<Migration> Discover(string directory)
    {
        var migrations = new List<Migration>();
        foreach (var path in Directory.GetFiles(directory, "*.sql").Where(p => !RepeatablePattern().IsMatch(System.IO.Path.GetFileName(p))))
        {
            var match = VersionedPattern().Match(System.IO.Path.GetFileName(path));
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

            var repeatables = await ApplyRepeatables(connection, directory, cancellationToken);
            return new MigrationResult(newlyApplied, alreadyApplied, repeatables);
        }
        finally
        {
            await Execute(connection, "SELECT pg_advisory_unlock(@key)", CancellationToken.None, ("key", AdvisoryLockKey));
        }
    }

    private async Task<IReadOnlyList<string>> ApplyRepeatables(NpgsqlConnection connection, string directory, CancellationToken cancellationToken)
    {
        await Execute(connection, """
            CREATE TABLE IF NOT EXISTS public.schema_repeatable (
              name       varchar(200) PRIMARY KEY,
              checksum   char(64) NOT NULL,
              applied_at timestamptz NOT NULL DEFAULT now()
            )
            """, cancellationToken);

        var applied = new List<string>();
        foreach (var path in Directory.GetFiles(directory, "R__*.sql").OrderBy(p => p, StringComparer.Ordinal))
        {
            var name = System.IO.Path.GetFileName(path);
            if (!RepeatablePattern().IsMatch(name))
            {
                throw new InvalidOperationException($"Repeatable script name '{name}' does not match R__name.sql.");
            }

            var checksum = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
            var current = await Scalar(connection, "SELECT checksum FROM public.schema_repeatable WHERE name = @name", cancellationToken, ("name", name));
            if (current is string existing && existing == checksum)
            {
                continue;
            }

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using (var command = new NpgsqlCommand(await File.ReadAllTextAsync(path, cancellationToken), connection, transaction) { CommandTimeout = 0 })
            {
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var record = new NpgsqlCommand("""
                INSERT INTO public.schema_repeatable (name, checksum) VALUES (@name, @checksum)
                ON CONFLICT (name) DO UPDATE SET checksum = EXCLUDED.checksum, applied_at = now()
                """, connection, transaction))
            {
                record.Parameters.AddWithValue("name", name);
                record.Parameters.AddWithValue("checksum", checksum);
                await record.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            applied.Add(name);
            log?.Invoke($"Applied repeatable {name}.");
        }

        return applied;
    }

    /// <summary>
    /// Creates the module login roles (local, test and CI only) when a password is given, then applies the module
    /// grants. Run as the owner after migrating.
    /// </summary>
    public async Task ApplyRoles(string rolesDirectory, string? password, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        if (password is not null)
        {
            var create = await File.ReadAllTextAsync(System.IO.Path.Combine(rolesDirectory, "create_login_roles.sql"), cancellationToken);

            // The password goes into a quoted literal inside format(%L); single quotes are doubled for the SQL string.
            await Execute(connection, create.Replace("{{PASSWORD}}", password.Replace("'", "''", StringComparison.Ordinal), StringComparison.Ordinal),
                cancellationToken);
        }

        await Execute(connection, await File.ReadAllTextAsync(System.IO.Path.Combine(rolesDirectory, "module_grants.sql"), cancellationToken), cancellationToken);
        log?.Invoke("Applied module role grants.");
    }

    /// <summary>
    /// Creates the platform operator tenant, its two platform administrators and the PLATFORM_ADMIN role
    /// (db/bootstrap/operator.sql). BR-061: exactly two distinct people, or nothing privileged could ever be approved.
    /// </summary>
    public async Task BootstrapOperator(string scriptPath, Guid operatorTenantId, IReadOnlyList<PlatformAdministrator> administrators,
        CancellationToken cancellationToken = default)
    {
        PlatformAdministrator.Validate(administrators);
        if (operatorTenantId == Guid.Empty)
        {
            throw new ArgumentException("The operator tenant ID must not be empty.");
        }

        var sql = (await File.ReadAllTextAsync(scriptPath, cancellationToken)).Replace("{{OPERATOR_TENANT_ID}}", operatorTenantId.ToString(), StringComparison.Ordinal);
        for (var i = 0; i < administrators.Count; i++)
        {
            var (n, admin) = (i + 1, administrators[i]);
            sql = sql
                .Replace($"{{{{ADMIN{n}_USER_ID}}}}", Guid.CreateVersion7().ToString(), StringComparison.Ordinal)
                .Replace($"{{{{ADMIN{n}_SUBJECT}}}}", Literal(admin.Subject), StringComparison.Ordinal)
                .Replace($"{{{{ADMIN{n}_USERNAME}}}}", Literal(admin.Username), StringComparison.Ordinal)
                .Replace($"{{{{ADMIN{n}_NAME}}}}", Literal(admin.DisplayName), StringComparison.Ordinal);
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await Execute(connection, sql, cancellationToken);
        log?.Invoke($"Bootstrapped operator tenant {operatorTenantId} with platform administrators {administrators[0].Username} and {administrators[1].Username}.");

        // Inside a JSON string inside a SQL string literal: JSON escapes quotes, backslashes and apostrophes (as \u0027).
        static string Literal(string value) => System.Text.Json.JsonEncodedText.Encode(value).ToString();
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

/// <summary>A platform administrator the bootstrap creates (BR-061): the IdP subject, the username and the display name.</summary>
public sealed record PlatformAdministrator(string Subject, string Username, string DisplayName)
{
    public const string Required =
        "The platform bootstrap needs two distinct platform administrators, named in the deployment configuration (BR-061): "
        + "--admin1-subject, --admin1-username, --admin1-name and --admin2-subject, --admin2-username, --admin2-name "
        + "(or INSYS_BOOTSTRAP_ADMIN1_SUBJECT, ..._USERNAME, ..._NAME and INSYS_BOOTSTRAP_ADMIN2_...). "
        + "With one administrator no privileged change can ever be approved, because every one needs a second person.";

    /// <summary>Exactly two, each complete, and two different people (subject and username).</summary>
    public static void Validate(IReadOnlyList<PlatformAdministrator> administrators)
    {
        if (administrators.Count != 2 || administrators.Any(a => string.IsNullOrWhiteSpace(a.Subject) || string.IsNullOrWhiteSpace(a.Username)
                || string.IsNullOrWhiteSpace(a.DisplayName)))
        {
            throw new ArgumentException($"{Required} Configured: {administrators.Count(a => !string.IsNullOrWhiteSpace(a.Subject))} complete administrator(s).");
        }

        if (string.Equals(administrators[0].Subject, administrators[1].Subject, StringComparison.Ordinal)
            || string.Equals(administrators[0].Username, administrators[1].Username, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"{Required} Both are configured as the same person ({administrators[0].Username}).");
        }
    }
}
