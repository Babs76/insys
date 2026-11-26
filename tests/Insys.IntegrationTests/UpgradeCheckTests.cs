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
using System.Net;
using Insys.Seed;
using Insys.Tenancy.Contracts;

namespace Insys.IntegrationTests;

/// <summary>
/// The upgrade check (Ade, 26 November 2026): "On an existing tenant, it lists every approval type with no role holding both the authority and the
/// function to decide it. The upgrade stops if the list isn't empty." The tool (tools/Insys.Seed --upgrade-check) runs as a platform operator through the
/// API, so these run it against the real API on an ACTIVE tenant, whose readiness (APPROVAL_AUTHORITY) the rule is.
/// </summary>
public sealed class UpgradeCheckTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private const string OperatorAdmin = "upgrade-operator";
    private static readonly DateOnly From = TestClock.Today.AddDays(-30);

    /// <summary>The approval types new since the Preview's back end (<c>ca1bf2b</c>), whose rows an OFTZA seeded then doesn't have.</summary>
    private static readonly string[] NewSinceThePreview = ["COMMISSION_CLAWBACK_INVOICE", "CREDIT_NOTE", "DEAD_LETTER_DISCARD", "DISCLOSURE_SETTING_CHANGE", "PAYOUT_RETRY"];

    private InsysApiFactory _factory = null!;
    private HttpClient _operator = null!;
    private HttpClient _checker = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = await fixture.Host();
        await fixture.Database.UserWithPermissions(DatabaseFixture.Operator, OperatorAdmin, TenancyPermissions.Read, TenancyPermissions.Create, TenancyPermissions.Update);
        _operator = _factory.ClientFor(DatabaseFixture.Operator, OperatorAdmin);
        await fixture.Database.UserWithPermissions(DatabaseFixture.Operator, OperatorAdmin + "-2", TenancyPermissions.Read, TenancyPermissions.Update);
        _checker = _factory.ClientFor(DatabaseFixture.Operator, OperatorAdmin + "-2");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task AnActiveTenantsRaisableTypeWithNoDelegationRow_IsListed_ARowForARoleWithoutTheDecidePermissionStillIs_ARoleWithBothClearsIt()
    {
        var (tenantId, _, _) = await Active("UPGCHK1");
        (await Listed(tenantId)).ShouldBeEmpty("every type has the clerk's row, and the clerk holds approval.approve");

        // The tenant's rows predate a raisable type: its row is missing (retired here).
        await fixture.Database.ExecuteAsOwner("UPDATE workflow.cfg_delegation_and_approval_authority SET record_status = 'RETIRED' WHERE tenant_id = @t AND code = 'AUTH-PAYOUT_RETRY'",
            ("t", tenantId));
        (await Listed(tenantId)).ShouldBe(["PAYOUT_RETRY"]);

        // Authority for a staff role that doesn't hold the function to decide (approval.approve): still listed.
        await Role(tenantId, "VIEWER", "dead_letter.read");
        await Authority(tenantId, "PAYOUT_RETRY", "VIEWER");
        (await Listed(tenantId)).ShouldBe(["PAYOUT_RETRY"]);

        // A role with both: cleared, and the check's report for this tenant says none.
        await Role(tenantId, "DECIDER", "approval.approve");
        await Authority(tenantId, "PAYOUT_RETRY", "DECIDER");
        (await Listed(tenantId)).ShouldBeEmpty();
    }

    /// <summary>
    /// The Preview's case (docs/plans/preview-rebuild-beta.md, section 8): a tenant seeded before the five types has no row for any of them, and the check lists
    /// exactly those; the plan's rows (the finance manager's four, the compliance officer's disclosure setting change), for roles that hold approval.approve
    /// as both templates did then, clear the list.
    /// </summary>
    [Fact]
    public async Task ATenantSeededBeforeTheFiveNewApprovalTypes_ListsExactlyThem_UntilThePlansRowsAreInForce()
    {
        var (tenantId, _, _) = await Active("UPGCHK2");
        foreach (var type in NewSinceThePreview)
        {
            await fixture.Database.ExecuteAsOwner("UPDATE workflow.cfg_delegation_and_approval_authority SET record_status = 'RETIRED' WHERE tenant_id = @t AND code = @c",
                ("t", tenantId), ("c", $"AUTH-{type}"));
        }

        (await Listed(tenantId)).ShouldBe(NewSinceThePreview);

        await Role(tenantId, "FINANCE_MANAGER", "approval.approve");
        await Role(tenantId, "COMPLIANCE_OFFICER", "approval.approve");
        foreach (var type in NewSinceThePreview)
        {
            await Authority(tenantId, type, type == "DISCLOSURE_SETTING_CHANGE" ? "COMPLIANCE_OFFICER" : "FINANCE_MANAGER");
        }

        (await Listed(tenantId)).ShouldBeEmpty();
    }

    /// <summary>
    /// Which tenants: a suspended tenant is checked (it is reactivated with what it had, without onboarding again); a terminated one isn't, nor an onboarding
    /// one before SHARED_OPERATIONAL_SETUP is completed (completing the step runs the same check), nor the operator tenant (OQ-205). The tool exits 1 while any
    /// list checked isn't empty.
    /// </summary>
    [Fact]
    public async Task ASuspendedTenantIsChecked_ATerminatedOneAndAnOnboardingOneBeforeItsStepAreNot_AndAListExitsOne()
    {
        var (tenantId, url, etag) = await Active("UPGCHK3");
        await fixture.Database.ExecuteAsOwner("UPDATE workflow.cfg_delegation_and_approval_authority SET record_status = 'RETIRED' WHERE tenant_id = @t AND code = 'AUTH-CREDIT_NOTE'",
            ("t", tenantId));
        var (onboarding, _) = await Register("UPGCHK4");

        var suspended = await _operator.Send(HttpMethod.Post, $"{url}/suspend", new { reason = "Upgrade check test" }, ifMatch: etag);
        suspended.StatusCode.ShouldBe(HttpStatusCode.NoContent, await suspended.Content.ReadAsStringAsync());
        var findings = await UpgradeCheck.Run(Platform(), DatabaseFixture.Operator);
        var finding = findings.Single(f => f.TenantId == tenantId);
        finding.Status.ShouldBe("Suspended");
        finding.Undecidable.ShouldBe(["CREDIT_NOTE"]);
        findings.Single(f => f.TenantId == onboarding).NotCheckedBecause.ShouldNotBeNull().ShouldContain("SHARED_OPERATIONAL_SETUP");
        findings.Where(f => f.TenantId == DatabaseFixture.Operator).ShouldAllBe(f => f.Undecidable == null);
        var report = new StringWriter();
        (await UpgradeCheck.RunAndReport(Platform(), DatabaseFixture.Operator, report, new StringWriter())).ShouldBe(UpgradeCheck.Listed);
        report.ToString().ShouldContain("UPGCHK3 Suspended: CREDIT_NOTE");

        var terminated = await _operator.Send(HttpMethod.Post, $"{url}/terminate", new { reason = "Upgrade check test" }, ifMatch: suspended.ETag());
        terminated.StatusCode.ShouldBe(HttpStatusCode.NoContent, await terminated.Content.ReadAsStringAsync());
        (await UpgradeCheck.Run(Platform(), DatabaseFixture.Operator)).Single(f => f.TenantId == tenantId).NotCheckedBecause.ShouldBe("terminated");
    }

    private Persona Platform() => new(_factory.ClientFor(DatabaseFixture.Operator, OperatorAdmin));

    private async Task<IReadOnlyList<string>> Listed(Guid tenantId) =>
        (await UpgradeCheck.Run(Platform(), DatabaseFixture.Operator)).Single(f => f.TenantId == tenantId).Undecidable.ShouldNotBeNull();

    /// <summary>A staff role in force with the functions within the permission.</summary>
    private async Task Role(Guid tenantId, string role, string permission)
    {
        await fixture.Database.SeedStaticFilled(tenantId, "iam.cfg_roles_and_permission_sets", "EFFECTIVE", role, From, ("role_code", role), ("permissions", DBNull.Value));
        await fixture.Database.GrantFunctionsWithin(tenantId, role, From, permission);
    }

    private Task Authority(Guid tenantId, string type, string role) =>
        fixture.Database.SeedStaticFilled(tenantId, "workflow.cfg_delegation_and_approval_authority", "EFFECTIVE", $"AUTH-{type}-{role}", From, ("role_code", role),
            ("transaction_type", type), ("limit_value", 1_000m), ("delegation_allowed", false));

    private async Task<(Guid TenantId, string Url, string ETag)> Active(string code)
    {
        var (tenantId, url) = await Register(code);
        await fixture.Database.SeedReadyTenant(tenantId, From);
        await fixture.Database.SeedActiveAdministrators(tenantId, code.ToLowerInvariant() + "-admin", "user.read");
        var etag = await _operator.CompleteOnboarding(_checker, url, (await _operator.GetAsync(url)).ETag());
        var activated = await _operator.Send(HttpMethod.Post, $"{url}/activate", new { reason = "Go live" }, ifMatch: etag);
        activated.StatusCode.ShouldBe(HttpStatusCode.NoContent, await activated.Content.ReadAsStringAsync());
        return (tenantId, url, activated.ETag());
    }

    private async Task<(Guid TenantId, string Url)> Register(string code)
    {
        var registered = await _operator.Send(HttpMethod.Post, "/api/v1/tenancy/tenants",
            new { tenantCode = code, legalName = code + " Ltd", isolationProfile = "B", timeZone = "Africa/Johannesburg" }, idempotent: true);
        registered.StatusCode.ShouldBe(HttpStatusCode.Created, await registered.Content.ReadAsStringAsync());
        var id = (await registered.Json()).GetProperty("id").GetGuid();
        return (id, $"/api/v1/tenancy/tenants/{id}");
    }
}
