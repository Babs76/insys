using Insys.DbMigrator;
using Insys.Testing;
using Npgsql;

namespace Insys.IntegrationTests;

public sealed class MigrationTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public void AllMigrations_ApplyToAFreshDatabase_DBA017()
    {
        fixture.Database.Migrations.Applied.Select(m => m.Version).ShouldBe(MigrationRunner.Discover(RepositoryPaths.Migrations).Select(m => m.Version));
        fixture.Database.Migrations.AlreadyApplied.ShouldBeEmpty();
    }

    [Fact]
    public async Task Migrations_CreateTheSpecifiedCoreAndStaticTables()
    {
        // V0001: 154 tables in 22 schemas (outbox per module included); V0002: 252 static data tables;
        // V0004: an inbox in 21 module schemas, and tenant.outbox_event and analytics.outbox_event (23);
        // V0007: iam.access_review_item; V0008: sdm.tenant_governance_setting. Partitions excluded.
        var tables = await fixture.Database.ScalarAsOwner<long>("""
            SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind IN ('r', 'p') AND NOT c.relispartition
              AND n.nspname NOT IN ('pg_catalog', 'information_schema', 'public', 'test_mod')
            """);
        tables.ShouldBe(154 + 252 + 23 + 2);
    }

    [Fact]
    public async Task Migrations_RerunIsANoOp_DBA017()
    {
        var result = await new MigrationRunner(fixture.Database.OwnerConnectionString).Migrate(RepositoryPaths.Migrations);

        result.Applied.ShouldBeEmpty();
        result.AlreadyApplied.Count.ShouldBe(MigrationRunner.Discover(RepositoryPaths.Migrations).Count);
    }

    [Fact]
    public async Task Migrations_AnEditedAppliedMigration_IsRefused_DBA017()
    {
        var copy = CopyMigrations();
        await File.AppendAllTextAsync(Path.Combine(copy, "V0003__monthly_partitions_2026_2027.sql"), "\n-- edited\n");

        var error = await Should.ThrowAsync<InvalidOperationException>(() => new MigrationRunner(fixture.Database.OwnerConnectionString).Migrate(copy));
        error.Message.ShouldContain("has changed since it was applied");
    }

    [Fact]
    public async Task Migrations_ANewMigrationOlderThanTheLatest_IsRefused_DBA017()
    {
        var empty = await fixture.Server.CreateEmptyDatabase();
        var directory = Path.Combine(Path.GetTempPath(), $"insys-migrations-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "V0001__first.sql"), "SELECT 1;");
        await File.WriteAllTextAsync(Path.Combine(directory, "V0003__third.sql"), "SELECT 1;");
        await new MigrationRunner(empty).Migrate(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "V0002__late.sql"), "SELECT 1;");

        var error = await Should.ThrowAsync<InvalidOperationException>(() => new MigrationRunner(empty).Migrate(directory));
        error.Message.ShouldContain("older than the latest applied migration");
    }

    [Fact]
    public async Task Partitions_ExistForEveryMonthOf2026And2027_WithTheParentPolicy_DBA023()
    {
        var missing = await fixture.Database.ScalarAsOwner<long>("""
            SELECT count(*) FROM generate_series(DATE '2026-01-01', DATE '2027-12-01', INTERVAL '1 month') AS m(month)
            CROSS JOIN unnest(ARRAY['audit.audit_event', 'finance.journal', 'finance.subledger_entry', 'rating.rating_request',
                                    'rules.decision_log', 'document.document_access_log', 'health.eligibility_decision',
                                    'integration.external_call_log']) AS p(parent)
            WHERE to_regclass(p.parent || '_' || to_char(m.month, 'YYYY_MM')) IS NULL
               OR NOT EXISTS (SELECT 1 FROM pg_policies pol
                              WHERE pol.schemaname || '.' || pol.tablename = p.parent || '_' || to_char(m.month, 'YYYY_MM')
                                AND pol.policyname = 'tenant_isolation')
            """);
        missing.ShouldBe(0);
    }

    [Fact]
    public async Task Partitions_ARowWithNoPartition_IsRejected_DBA023()
    {
        var error = await Should.ThrowAsync<PostgresException>(() => fixture.Database.ExecuteAsOwner("""
            INSERT INTO audit.audit_event (tenant_id, id, created_at, created_by, event_type, actor_type, actor_id, operation, target_type, target_id, outcome, occurred_at, correlation_id)
            VALUES (gen_random_uuid(), gen_random_uuid(), now(), 'x', 'X', 'USER', 'x', 'X', 'X', 'x', 'SUCCESS', TIMESTAMPTZ '2031-01-01', gen_random_uuid())
            """));
        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
    }

    private static string CopyMigrations()
    {
        var copy = Path.Combine(Path.GetTempPath(), $"insys-migrations-{Guid.NewGuid():N}");
        Directory.CreateDirectory(copy);
        foreach (var file in Directory.GetFiles(RepositoryPaths.Migrations, "*.sql"))
        {
            File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
        }

        return copy;
    }
}
