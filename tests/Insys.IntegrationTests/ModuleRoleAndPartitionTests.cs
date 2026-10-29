/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : IntegrationTests
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
using Insys.Audit.Infrastructure.Partitions;
using Insys.BuildingBlocks.Application.Context;
using Insys.Testing;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Insys.IntegrationTests;

/// <summary>One database role per module (OQ-004, DBA-004) and partition maintenance (OQ-003, OQ-005, DBA-023).</summary>
public sealed class ModuleRoleAndPartitionTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private const string OutboxInsert =
        "INSERT INTO {0}.outbox_event (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, aggregate_type, aggregate_id, event_type, "
        + "event_version, payload, occurred_at, correlation_id, publish_attempts) "
        + "VALUES (@t, gen_random_uuid(), 1, now(), 'x', now(), 'x', 'X', gen_random_uuid(), 'x', 1, '{{}}', now(), gen_random_uuid(), 0)";

    public static TheoryData<string, string> CrossSchemaWrites => new()
    {
        { "insys_life", string.Format(System.Globalization.CultureInfo.InvariantCulture, OutboxInsert, "general") },
        { "insys_integration", "UPDATE iam.user_account SET status = 'DISABLED'" },
        { "insys_iam", "INSERT INTO audit.audit_event (tenant_id, id, created_at, created_by, event_type, actor_type, actor_id, operation, target_type, target_id, outcome, occurred_at, correlation_id) VALUES (@t, gen_random_uuid(), now(), 'x', 'X', 'USER', 'x', 'X', 'X', 'x', 'SUCCESS', now(), gen_random_uuid())" },
        { "insys_tenant", "DELETE FROM sdm.change_request" },
        { "insys_api", "SELECT count(*) FROM integration.inbox_message" },
    };

    [Theory]
    [MemberData(nameof(CrossSchemaWrites))]
    public async Task ModuleRole_CannotTouchAnotherModulesSchema_DBA004(string role, string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.Database.ConnectionFor(role));
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTenant(connection, transaction, DatabaseFixture.TenantA);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("t", DatabaseFixture.TenantA);

        var error = await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        error.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task ModuleRole_CanWriteItsOwnSchema_DBA004()
    {
        await using var connection = new NpgsqlConnection(fixture.Database.ConnectionFor("insys_life"));
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTenant(connection, transaction, DatabaseFixture.TenantA);
        await using var command = new NpgsqlCommand(string.Format(System.Globalization.CultureInfo.InvariantCulture, OutboxInsert, "life"), connection, transaction);
        command.Parameters.AddWithValue("t", DatabaseFixture.TenantA);

        (await command.ExecuteNonQueryAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task ApiRole_CanUseOnlyIdempotencyKeys_AndReadAndAddFieldDataKeys_INT003()
    {
        // Idempotency keys (INT-003), and the envelope encryption register (V0023, ADR-018): read and add, never change or remove.
        var privileges = await fixture.Database.ScalarAsOwner<long>("""
            SELECT count(*) FROM information_schema.role_table_grants
            WHERE grantee = 'insys_api' AND NOT (table_schema = 'integration' AND table_name = 'idempotency_key')
              AND NOT (table_schema = 'integration' AND table_name = 'field_data_key' AND privilege_type IN ('SELECT', 'INSERT'))
            """);
        privileges.ShouldBe(0);
    }

    /// <summary>
    /// SRS §27.4 at the database: every table with a tenant column, in every schema, is under a forced tenant policy
    /// (TEN-004). Platform-global static data (scope G) is exempt: its check constraint keeps the tenant column NULL, so
    /// it holds no tenant's data.
    /// </summary>
    [Fact]
    public async Task EveryTenantTable_HasRowLevelSecurityEnabledAndForced_WithATenantPolicy_TEN004()
    {
        var unprotected = await fixture.Database.ScalarAsOwner<string>("""
            SELECT coalesce(string_agg(n.nspname || '.' || c.relname, ', ' ORDER BY n.nspname, c.relname), '')
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_attribute a ON a.attrelid = c.oid AND a.attname = 'tenant_id' AND NOT a.attisdropped
            WHERE c.relkind IN ('r', 'p') AND n.nspname NOT IN ('pg_catalog', 'information_schema', 'public', 'test_mod')
              AND NOT EXISTS (SELECT 1 FROM pg_constraint k WHERE k.conrelid = c.oid AND k.contype = 'c'
                                AND pg_get_constraintdef(k.oid) = 'CHECK ((tenant_id IS NULL))')
              AND (NOT c.relrowsecurity OR NOT c.relforcerowsecurity
                   OR NOT EXISTS (SELECT 1 FROM pg_policies p WHERE p.schemaname = n.nspname AND p.tablename = c.relname
                                    AND p.qual LIKE '%app.tenant_id%'))
            """);
        unprotected.ShouldBeEmpty();
    }

    [Fact]
    public async Task V0003Partitions_HaveRowLevelSecurityEnabledAndForced_WithTheTenantPolicy_DBA023()
    {
        var missing = await fixture.Database.ScalarAsOwner<long>("""
            SELECT count(*) FROM pg_inherits i
            JOIN pg_class c ON c.oid = i.inhrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE i.inhparent::regclass::text = ANY(audit.partitioned_tables())
              AND (NOT c.relrowsecurity OR NOT c.relforcerowsecurity
                   OR NOT EXISTS (SELECT 1 FROM pg_policies p WHERE p.schemaname = n.nspname AND p.tablename = c.relname AND p.policyname = 'tenant_isolation'
                                    AND p.qual LIKE '%current_setting(''app.tenant_id''::text)%' AND p.with_check LIKE '%current_setting(''app.tenant_id''::text)%'))
            """);
        missing.ShouldBe(0);
    }

    [Fact]
    public async Task Partition_QueriedDirectlyAsTheModuleRole_ReturnsNoOtherTenantsRows_DBA023()
    {
        var partition = $"audit.audit_event_{DateTime.UtcNow:yyyy_MM}";
        foreach (var tenant in new[] { DatabaseFixture.TenantA, DatabaseFixture.TenantB })
        {
            await fixture.Database.ExecuteAsOwner("""
                INSERT INTO audit.audit_event (tenant_id, id, created_at, created_by, event_type, actor_type, actor_id, operation, target_type, target_id, outcome, occurred_at, correlation_id)
                VALUES (@t, gen_random_uuid(), now(), 'seed', 'TEST', 'USER', 'seed', 'PartitionCheck', 'Test', 'x', 'SUCCESS', now(), gen_random_uuid())
                """, ("t", tenant));
        }

        await using var connection = new NpgsqlConnection(fixture.Database.ConnectionFor("insys_audit"));
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTenant(connection, transaction, DatabaseFixture.TenantA);
        await using var command = new NpgsqlCommand($"SELECT DISTINCT tenant_id FROM {partition} WHERE operation = 'PartitionCheck'", connection, transaction);
        var tenants = new List<Guid>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                tenants.Add(reader.GetGuid(0));
            }
        }

        tenants.ShouldBe([DatabaseFixture.TenantA]);
    }

    [Fact]
    public async Task PartitionJob_CreatesPartitionsAhead_WithRlsAndGrants_DBA023()
    {
        var maintainer = Maintainer(monthsAhead: 18);

        var created = await maintainer.EnsurePartitions(CancellationToken.None);

        created.ShouldNotBeEmpty();
        created.ShouldAllBe(p => p.Contains("_20"));
        var newest = created.Where(p => p.StartsWith("audit.audit_event_", StringComparison.Ordinal)).Max()!;
        (await fixture.Database.ScalarAsOwner<bool>($"SELECT relforcerowsecurity FROM pg_class WHERE oid = '{newest}'::regclass")).ShouldBeTrue();

        // The module role reaches the new partition through default privileges, and still only within its tenant.
        await using var connection = new NpgsqlConnection(fixture.Database.ConnectionFor("insys_audit"));
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTenant(connection, transaction, DatabaseFixture.TenantA);
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {newest}", connection, transaction);
        (await command.ExecuteScalarAsync()).ShouldBe(0L);

        (await maintainer.EnsurePartitions(CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task PartitionHealth_IsHealthy_WhenCoveredForTwoMonths_AndDegradedOtherwise_DBA023()
    {
        var maintainer = Maintainer(monthsAhead: 3);
        await maintainer.EnsurePartitions(CancellationToken.None);
        var horizon = (await maintainer.Horizon(CancellationToken.None)).Values.Min()!.Value;

        var now = new HealthCheckContext();
        (await Check(maintainer, horizon.AddMonths(-3)).CheckHealthAsync(now)).Status.ShouldBe(HealthStatus.Healthy);
        (await Check(maintainer, horizon.AddMonths(-1)).CheckHealthAsync(now)).Status.ShouldBe(HealthStatus.Degraded);
    }

    private PartitionMaintainer Maintainer(int monthsAhead, DateOnly? today = null) =>
        new(NpgsqlDataSource.Create(fixture.Database.ConnectionFor("insys_audit")), Options.Create(new PartitionOptions { MonthsAhead = monthsAhead }),
            new SystemClock(new FakeTimeProvider(new DateTimeOffset((today ?? DateOnly.FromDateTime(DateTime.UtcNow)).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero))));

    /// <summary>
    /// V0093 (one time source, 29 October 2026): partitions are made for the application's months, the months its rows are
    /// dated in, from the date the application passes, never the database's own date. Here the application is 40 months
    /// ahead of the database: maintenance makes the partitions those rows need.
    /// </summary>
    [Fact]
    public async Task PartitionMaintenance_FollowsTheApplicationsClock_NotTheDatabases_V0093()
    {
        var ahead = DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(40);
        var month = new DateOnly(ahead.Year, ahead.Month, 1).AddMonths(2);
        var name = $"audit.audit_event_{month:yyyy_MM}";
        (await fixture.Database.ScalarAsOwner<bool>($"SELECT to_regclass('{name}') IS NULL")).ShouldBeTrue("not made by the database's clock");

        var created = await Maintainer(monthsAhead: 3, today: ahead).EnsurePartitions(CancellationToken.None);
        created.ShouldContain(name);
        (await fixture.Database.ScalarAsOwner<bool>($"SELECT to_regclass('{name}') IS NOT NULL")).ShouldBeTrue();
    }

    private static PartitionHorizonHealthCheck Check(PartitionMaintainer maintainer, DateOnly today) =>
        new(maintainer, new SystemClock(new FakeTimeProvider(new DateTimeOffset(today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero))),
            Options.Create(new PartitionOptions()));

    private static async Task SetTenant(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid tenant)
    {
        await using var command = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, true)", connection, transaction);
        command.Parameters.AddWithValue("t", tenant.ToString());
        await command.ExecuteScalarAsync();
    }
}
