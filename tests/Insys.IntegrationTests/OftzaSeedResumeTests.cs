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
using System.Text.RegularExpressions;
using Insys.Seed;
using Insys.Testing;
using Shouldly;
using Xunit;

namespace Insys.IntegrationTests;

/// <summary>A seed stopped part-way, after the API has answered but before the seed heard, can be run again to completion, with every record once.</summary>
public sealed class OftzaSeedResumeTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private sealed class Interrupted() : Exception("Stopped part-way, for the test.");

    [Fact]
    public async Task ARunInterruptedPartWay_CanBeRunAgainToCompletion_WithEveryRecordOnce()
    {
        var db = fixture.Database;
        var host = await fixture.Host();
        string[] platform = ["tenant.read", "tenant.create", "tenant.update", "user.read", "user.create", "approval.read", "approval.approve", "static_data.read", "static_data.create", "static_data.update", "static_data.approve"];
        await db.UserWithPermissions(DatabaseFixture.Operator, "seed-platform-1", platform);
        var second = await db.UserWithPermissions(DatabaseFixture.Operator, "seed-platform-2", platform);
        var role = await db.RoleWithPermissions(DatabaseFixture.Operator);
        await db.AssignRole(DatabaseFixture.Operator, second, role);
        await db.SeedStatic(DatabaseFixture.Operator, "workflow.cfg_delegation_and_approval_authority", "EFFECTIVE", "AUTH-SEED-ADM", new DateOnly(2026, 1, 1), ("role_code", role),
            ("transaction_type", "TENANT_ADMINISTRATOR"), ("limit_value", 1m), ("delegation_allowed", false));

        // Each kind of step is stopped once, just after the API has done it and before the seed hears.
        var unstopped = new List<Regex>(new[]
        {
            "tenancy/tenants$", "tenant-administrators$", "/static/[^/]+$", "/imports$", "/imports/[^/]+/submit$", "/imports/[^/]+/activate$", "/versions/\\d+/submit$", "/versions/\\d+/approve$", "/versions/\\d+/activate$",
            "workflow/approvals/[^/]+/approve$", "identity/users$", "/assign-role$", "party/parties$", "/identifiers$", "/contact-points$", "product-factory/products$", "/validate$", "/simulations$",
            "/periods/[^/]+/open$", "callback-endpoints$", "/smoke-run$", "/smoke-run/approve$", "/complete-onboarding-step$", "/bank-accounts$", "/addresses$", "health/schemes$", "health/memberships$", "life/proposals$", "/life/proposals/[^/]+/submit$", "/underwriting-cases/[^/]+/decision$", "life/policies$", "life/migration/policies$", "general/risk-objects$", "general/quotes$", "general/proposals$", "general/policies$", "general/migration/policies$", "/cancel$", "/suspend$", "/terminate$", "outstanding-premiums$", "general/claims$", "/register$", "/reserves$", "/settlement$", "life/claims$", "health/claims$", "payment-runs$", "payout-batches$", "/refunds$", "provider-statements/import$", "tax-returns$", "/due-diligence/checks$", "/disclosure-acknowledgements$", "/revoke-role$", "/disable$", "/disable$",
        }.Select(p => new Regex(p)));
        var kinds = unstopped.Count;
        void Stop(string call)
        {
            if (call.StartsWith("POST ", StringComparison.Ordinal) && unstopped.FirstOrDefault(p => p.IsMatch(call)) is { } kind)
            {
                unstopped.Remove(kind);
                throw new Interrupted();
            }
        }

        var interruptions = 0;
        OftzaSeed seed;
        while (true)
        {
            seed = new OftzaSeed(new SeedContext(new Persona(host.ClientFor(DatabaseFixture.Operator, "seed-platform-1"), Stop), new Persona(host.ClientFor(DatabaseFixture.Operator, "seed-platform-2"), Stop),
                (tenant, subject) => new Persona(host.ClientFor(tenant, subject, "insys-web"), Stop), () => host.DrainOutbox()));
            try
            {
                await seed.Run();
                break;
            }
            catch (Interrupted)
            {
                interruptions++;
            }
        }

        unstopped.Select(p => p.ToString()).ShouldBeEmpty("every kind of step was stopped once");
        interruptions.ShouldBe(kinds);
        var t = ("t", (object)seed.TenantId);
        (await db.ScalarAsOwner<string>("SELECT status FROM tenant.tenant WHERE id = @t", t)).ShouldBe("ACTIVE");
        (await db.ScalarAsOwner<long>("SELECT count(*) FROM party.party WHERE tenant_id = @t", t)).ShouldBe(30L, "no party twice");
        (await db.ScalarAsOwner<long>("SELECT count(*) FROM life.policy WHERE tenant_id = @t", t)).ShouldBe(12L, "no policy twice");
        (await db.ScalarAsOwner<long>("SELECT count(*) FROM general.policy WHERE tenant_id = @t", t)).ShouldBe(12L, "no policy twice");
        (await db.ScalarAsOwner<long>("SELECT count(*) FROM general.claim WHERE tenant_id = @t", t)).ShouldBe(7L, "no claim twice");
        (await db.ScalarAsOwner<long>("SELECT count(*) FROM billing.refund_request WHERE tenant_id = @t", t)).ShouldBe(1L, "no refund twice");
        (await db.ScalarAsOwner<long>("SELECT count(*) FROM health.membership WHERE tenant_id = @t", t)).ShouldBe(12L, "no member twice");
        (await db.ScalarAsOwner<long>("SELECT count(*) FROM iam.user_account WHERE tenant_id = @t AND username LIKE 'oftza-seed-loader-%' AND status <> 'DISABLED'", t)).ShouldBe(0L);
        (await db.ScalarAsOwner<long>("SELECT count(*) FROM iam.user_role_assignment WHERE tenant_id = @t AND status = 'PENDING_APPROVAL'", t)).ShouldBe(0L);
    }
}
