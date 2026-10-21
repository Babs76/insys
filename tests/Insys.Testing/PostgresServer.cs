using Insys.DbMigrator;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Insys.Testing;

/// <summary>
/// One PostgreSQL server per test run. By default a Testcontainers postgres:18 container. Set INSYS_TEST_PG to an
/// administrator connection string to use an existing server instead (for machines without Docker); each test
/// class still gets its own freshly migrated database.
/// </summary>
public sealed class PostgresServer : IAsyncLifetime
{
    public const string AppRole = "insys_app_test";
    public const string AppRolePassword = "insys_app_test_only";
    private PostgreSqlContainer? _container;

    public string AdminConnectionString { get; private set; } = "";

    public string ServerDescription { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable("INSYS_TEST_PG");
        if (!string.IsNullOrWhiteSpace(external))
        {
            AdminConnectionString = external;
        }
        else
        {
            _container = new PostgreSqlBuilder("postgres:18").Build();
            await _container.StartAsync();
            AdminConnectionString = _container.GetConnectionString();
        }

        await using var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync();

        // The role is cluster-wide: create it once here, before test classes create their databases in parallel.
        await using (var role = new NpgsqlCommand($"""
            DO $$ BEGIN
              IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = '{AppRole}') THEN
                CREATE ROLE {AppRole} LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE PASSWORD '{AppRolePassword}';
              END IF;
            END $$
            """, connection))
        {
            await role.ExecuteNonQueryAsync();
        }

        await using var version = new NpgsqlCommand("SHOW server_version", connection);
        ServerDescription = $"PostgreSQL {await version.ExecuteScalarAsync()}";
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>Creates an empty database and returns its owner connection string.</summary>
    public async Task<string> CreateEmptyDatabase()
    {
        var name = $"insys_test_{Guid.NewGuid():N}"[..30];
        await using (var connection = new NpgsqlConnection(AdminConnectionString))
        {
            await connection.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {name}", connection);
            await create.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = name, Pooling = false }.ConnectionString;
    }

    /// <summary>Creates an empty database, applies every migration as the owner and grants the non-owner app role.</summary>
    public async Task<TestDatabase> CreateMigratedDatabase()
    {
        var owner = await CreateEmptyDatabase();
        var name = new NpgsqlConnectionStringBuilder(owner).Database!;
        var runner = new MigrationRunner(owner);
        var result = await runner.Migrate(RepositoryPaths.Migrations);
        await runner.GrantAppRole(RepositoryPaths.AppRoleGrants, AppRole, AppRolePassword);

        var app = new NpgsqlConnectionStringBuilder(AdminConnectionString)
        {
            Database = name,
            Username = AppRole,
            Password = AppRolePassword,
        }.ConnectionString;
        return new TestDatabase(name, owner, app, result);
    }
}

/// <param name="OwnerConnectionString">The migration owner. Superusers bypass row-level security: use it only to seed and inspect.</param>
/// <param name="AppConnectionString">The non-owner application role, subject to row-level security.</param>
public sealed record TestDatabase(string Name, string OwnerConnectionString, string AppConnectionString, MigrationResult Migrations)
{
    public async Task ExecuteAsOwner(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(OwnerConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    public async Task<T?> ScalarAsOwner<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(OwnerConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)Convert.ChangeType(result, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }

    public Task SeedTenant(Guid tenantId, string code) =>
        ExecuteAsOwner("""
            INSERT INTO tenant.tenant (id, tenant_code, legal_name, isolation_profile, status, created_at)
            VALUES (@id, @code, @name, 'A', 'ACTIVE', now())
            """, ("id", tenantId), ("code", code), ("name", $"Test tenant {code}"));
}
