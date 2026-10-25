/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Testing
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
    /// <summary>The password of every module login role in test databases (db/roles/create_login_roles.sql).</summary>
    public const string RolePassword = "insys_test_only";

    /// <summary>The role of the test-only module in schema test_mod.</summary>
    public const string TestModuleRole = "insys_test_mod";
    private readonly SemaphoreSlim _migrationLock = new(1, 1);
    private PostgreSqlContainer? _container;
    private (string Name, MigrationResult Result)? _template;

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
            // Each test host opens a pool per module role (22 modules), and test classes run in parallel: the default
            // 100 connections ran out once Phase 2 added modules.
            _container = new PostgreSqlBuilder("postgres:18").WithCommand("-c", "max_connections=1000").Build();
            await _container.StartAsync();
            AdminConnectionString = _container.GetConnectionString();
        }

        await using var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync();

        // Roles are cluster-wide: create the test module's role once here, before test classes run in parallel.
        await using (var role = new NpgsqlCommand($"""
            DO $$ BEGIN
              IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = '{TestModuleRole}') THEN
                CREATE ROLE {TestModuleRole} LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE PASSWORD '{RolePassword}';
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

        _migrationLock.Dispose();
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

    /// <summary>
    /// A freshly migrated database: a copy of a template database that is migrated once per server (every migration as the
    /// owner, then the module role grants). Copying is much faster than migrating each test class's database, and keeps the
    /// heavy DDL of the migrations out of the time other test classes run.
    /// </summary>
    public async Task<TestDatabase> CreateMigratedDatabase()
    {
        var name = $"insys_test_{Guid.NewGuid():N}"[..30];
        MigrationResult result;
        await _migrationLock.WaitAsync();
        try
        {
            if (_template is null)
            {
                // Migrations create cluster-wide roles (V0002 creates insys_platform_admin), so they run once, here.
                var templateOwner = await CreateEmptyDatabase();
                var runner = new MigrationRunner(templateOwner);
                var migrated = await runner.Migrate(RepositoryPaths.Migrations);
                await runner.ApplyRoles(RepositoryPaths.Roles, RolePassword);
                NpgsqlConnection.ClearAllPools();
                _template = (new NpgsqlConnectionStringBuilder(templateOwner).Database!, migrated);
            }

            result = _template.Value.Result;

            // FILE_COPY copies the template's files; one copy at a time, as a template may have no other session.
            await using var connection = new NpgsqlConnection(AdminConnectionString);
            await connection.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {name} TEMPLATE {_template.Value.Name} STRATEGY FILE_COPY", connection);
            await create.ExecuteNonQueryAsync();
        }
        finally
        {
            _migrationLock.Release();
        }

        var owner = new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = name, Pooling = false }.ConnectionString;

        // Small pools that release idle connections quickly keep many short-lived test hosts within the server's limit.
        var template = new NpgsqlConnectionStringBuilder(AdminConnectionString)
            {
                Database = name, Username = "ROLE", Password = RolePassword, MaxPoolSize = 10, ConnectionIdleLifetime = 5, ConnectionPruningInterval = 1,
            }
            .ConnectionString.Replace("Username=ROLE", "Username={role}", StringComparison.Ordinal);
        return new TestDatabase(name, owner, template, result);
    }
}

/// <param name="OwnerConnectionString">The migration owner. Superusers bypass row-level security: use it only to seed and inspect.</param>
/// <param name="RoleConnectionTemplate">A connection string with {role} for any module login role; all are non-owners subject to row-level security.</param>
public sealed record TestDatabase(string Name, string OwnerConnectionString, string RoleConnectionTemplate, MigrationResult Migrations)
{
    /// <summary>A connection string for a module role, for example insys_integration.</summary>
    public string ConnectionFor(string role) => RoleConnectionTemplate.Replace("{role}", role, StringComparison.Ordinal);

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

    /// <summary>An Active tenant entitled to every feature (ADR-026 §2), so a test needs no entitlement unless it is about them.</summary>
    public async Task SeedTenant(Guid tenantId, string code)
    {
        await ExecuteAsOwner("""
            INSERT INTO tenant.tenant (id, tenant_code, legal_name, isolation_profile, status, created_at)
            VALUES (@id, @code, @name, 'A', 'ACTIVE', now())
            """, ("id", tenantId), ("code", code), ("name", $"Test tenant {code}"));
        await EntitleToAllFeatures(code);
    }

    /// <summary>Global entitlement rows naming the tenant by its code, one per feature (OQ-091).</summary>
    public Task EntitleToAllFeatures(string tenantCode) =>
        ExecuteAsOwner("""
            INSERT INTO tenant.cfg_feature_entitlements (tenant_id, code, version, effective_from, record_status, created_by, change_reason, tenant, feature, enabled)
            SELECT NULL, @code || ':' || f, 1, DATE '2000-01-01', 'EFFECTIVE', 'seed', 'test seed', @code, f, true
            FROM unnest(ARRAY['LIFE', 'GENERAL', 'HEALTH', 'REINSURANCE', 'BROKER_PORTAL', 'PROVIDER_PORTAL', 'CUSTOMER_PORTAL']) AS f
            ON CONFLICT DO NOTHING
            """, ("code", tenantCode));
}
