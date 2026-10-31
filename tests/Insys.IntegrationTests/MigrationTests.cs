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
        // V0007: iam.access_review_item; V0008: sdm.tenant_governance_setting; V0012: finance.accounting_period;
        // V0016: general.quote_risk and general.quote_coverage; V0018: party.bank_account; V0019: two Health static tables
        // (ADR-012) and life.claim_payee; V0021: analytics.event_log (ADR-015); V0023: integration.field_data_key (ADR-018);
        // V0026: iam.tenant_administrator_request (OQ-067); V0027: party.party_search_log (GAP-022); V0030: party.search_log_hold (OQ-073);
        // V0031: integration.parked_delivery and integration.parking_setting (ADR-022); V0032: claims.fraud_evaluation (F009);
        // V0033: payment.callback_endpoint and payment.callback_receipt (ADR-023); V0045: iam.user_subject_link (external-user access);
        // V0050: general.aggregation_alert (GAP-051); V0051: twelve communication tables (outbox, inbox, four static, six of its own);
        // V0052: five function catalogue tables (ADR-026); V0055: analytics.release_task; V0056: workflow.cfg_approval_expiry;
        // V0059 drops payment.cfg_payment_release_limits (OQ-112); V0060: six case tables and six case static tables (Wave 2);
        // V0062: iam.tenant_administrator_person (BR-013); V0064: six status histories and party.cfg_record_retention_policies (D-34).
        // V0066: document.record_hold (D-34); V0071: party.party_number_counter (BR-029); V0073: general.policy_effect (BR-038);
        // V0075: general.cfg_claim_party_roles (OQ-122); V0078: general.cfg_product_billing_frequencies (BR-095 follow-up);
        // V0091: party.due_diligence_check (F001); V0092: party.due_diligence, screening_hit, beneficial_owner and five due diligence configuration tables;
        // V0094: health.cfg_service_types (BR-105); V0095: general.cfg_grace_period_rules, general.cfg_short_period_scales, health.cfg_contribution_grace_rules (F006, F007);
        // V0096: general.cfg_renewal_review_rules, cfg_claims_experience_factors, cfg_renewal_settings, renewal, renewal_check (Wave 4 §4.2);
        // V0099: general.cfg_non_renewal_reasons (Wave 4 §4.3); V0100: general.cfg_retention_offers, retention_case, retention_offer, analytics.fact_renewals (Wave 4 §4.3).
        // Partitions excluded.
        var tables = await fixture.Database.ScalarAsOwner<long>("""
            SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind IN ('r', 'p') AND NOT c.relispartition
              AND n.nspname NOT IN ('pg_catalog', 'information_schema', 'public', 'test_mod')
            """);
        tables.ShouldBe(154 + 252 + 23 + 3 + 2 + 1 + 3 + 1 + 1 + 1 + 1 + 1 + 2 + 1 + 2 + 1 + 1 + 12 + 5 + 1 + 1 - 1 + 12 + 1 + 7 + 1 + 1 + 1 + 1 + 1 + 1 + 8 + 1 + 3 + 5 + 1 + 4 + 5 + 6 + 4 + 1 + 1 + 2 + 3 + 1 + 3);
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
