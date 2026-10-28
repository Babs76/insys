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

namespace Insys.IntegrationTests;

/// <summary>
/// Wave 2 batch 6's backfills: a payout batch's build time and a reconciliation exception's raise time come from when
/// they were created (V0083); a commission entitlement takes its plan version's basis (V0085); a GL export's sent time
/// comes from when it was created (V0086). What was never recorded (who sent, approvals, resolutions) stays unknown.
/// </summary>
public sealed class Batch6BackfillMigrationTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task BuildRaiseAndSendTimes_TakeTheirCreation_AndEntitlementsTheirPlansBasis_V0083_V0085_V0086()
    {
        var connection = await fixture.Server.CreateEmptyDatabase();
        var directory = Path.Combine(Path.GetTempPath(), $"insys-v0083-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var source = Path.Combine(RepositoryPaths.Root, "db", "migrations");
        foreach (var file in Directory.GetFiles(source, "V*.sql").Where(f => string.CompareOrdinal(Path.GetFileName(f), "V0083") < 0))
        {
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        }

        var database = new TestDatabase("v0083", connection, connection, await new MigrationRunner(connection).Migrate(directory));
        var rows = new RowFiller(database, DateTimeOffset.UtcNow);
        var tenant = Guid.NewGuid();
        var created = new DateTimeOffset(2026, 10, 1, 9, 30, 0, TimeSpan.Zero);
        var batch = await rows.Insert(tenant, "payment.payout_batch", ("created_at", created), ("status", "BUILT"), ("purpose", "REFUND"), ("currency", "ZAR"));
        var exception = await rows.Insert(tenant, "payment.reconciliation_exception", ("created_at", created), ("status", "OPEN"), ("exception_type", "UNMATCHED_LINE"));
        var export = await rows.Insert(tenant, "finance.gl_export_batch", ("created_at", created), ("status", "SENT"));
        await database.SeedStaticFilled(tenant, "commission.cfg_commission_plans", "EFFECTIVE", "PLAN-F", new DateOnly(2026, 1, 1), ("plan_code", "PLAN-F"),
            ("basis", "FIXED"), ("rate", 25m), ("clawback_rule", "UNEARNED"));
        var planVersion = await database.ScalarAsOwner<int>("SELECT version FROM commission.cfg_commission_plans WHERE tenant_id = @t AND plan_code = 'PLAN-F'", ("t", tenant));
        var entitlement = await rows.Insert(tenant, "commission.commission_entitlement", ("plan_code", "PLAN-F"), ("plan_version", planVersion), ("status", "ACCRUED"));
        var orphan = await rows.Insert(tenant, "commission.commission_entitlement", ("plan_code", "PLAN-GONE"), ("plan_version", 1), ("status", "ACCRUED"));

        foreach (var version in new[] { "V0083", "V0084", "V0085", "V0086" })
        {
            var file = Directory.GetFiles(source, $"{version}__*.sql").Single();
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        }

        await new MigrationRunner(connection).Migrate(directory);

        (await database.ScalarAsOwner<DateTime>("SELECT built_at FROM payment.payout_batch WHERE id = @id", ("id", batch))).ShouldBe(created.UtcDateTime);
        (await database.ScalarAsOwner<Guid?>("SELECT approval_id FROM payment.payout_batch WHERE id = @id", ("id", batch))).ShouldBeNull();
        (await database.ScalarAsOwner<DateTime>("SELECT raised_at FROM payment.reconciliation_exception WHERE id = @id", ("id", exception))).ShouldBe(created.UtcDateTime);
        (await database.ScalarAsOwner<DateTime?>("SELECT resolved_at FROM payment.reconciliation_exception WHERE id = @id", ("id", exception))).ShouldBeNull();
        (await database.ScalarAsOwner<DateTime>("SELECT sent_at FROM finance.gl_export_batch WHERE id = @id", ("id", export))).ShouldBe(created.UtcDateTime);
        (await database.ScalarAsOwner<Guid?>("SELECT sent_by FROM finance.gl_export_batch WHERE id = @id", ("id", export))).ShouldBeNull();
        (await database.ScalarAsOwner<string>("SELECT rate_basis FROM commission.commission_entitlement WHERE id = @id", ("id", entitlement))).ShouldBe("FIXED");
        (await database.ScalarAsOwner<string?>("SELECT rate_basis FROM commission.commission_entitlement WHERE id = @id", ("id", orphan))).ShouldBeNull();
    }
}
