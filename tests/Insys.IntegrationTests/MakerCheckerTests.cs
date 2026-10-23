using System.Net;
using Insys.Identity.Contracts;
using Insys.Integration.Contracts;
using Insys.StaticData.Contracts;
using Insys.Workflow.Contracts;

namespace Insys.IntegrationTests;

/// <summary>
/// The two maker-checker gaps of the Phase 2 review (SRS-002; OQ-038, OQ-046): a privileged role assignment and
/// switching Tier 2 maker-checker off each wait for a second person's approval in Workflow. One person alone cannot
/// complete either.
/// </summary>
public sealed class MakerCheckerTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private static readonly Guid Tenant = DatabaseFixture.TenantA;
    private static readonly string[] AdminPermissions =
    [
        IdentityPermissions.UserRead, IdentityPermissions.UserCreate, IdentityPermissions.UserUpdate, WorkflowPermissions.ApprovalRead,
        WorkflowPermissions.ApprovalApprove, StaticDataGovernancePermissions.SettingRead, StaticDataGovernancePermissions.SettingUpdate,
    ];

    private const string PrivilegedRole = "NIGHT_OPERATOR";

    private InsysApiFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = await fixture.Host();
        var db = fixture.Database;
        await db.UserWithPermissions(Tenant, "admin-one", AdminPermissions);
        var second = await db.UserWithPermissions(Tenant, "admin-two", AdminPermissions);

        // A role is privileged when an authentication policy requires MFA for it.
        await db.ExecuteAsOwner("""
            INSERT INTO iam.cfg_roles_and_permission_sets (tenant_id, code, version, effective_from, record_status, created_by, change_reason, role_code, permissions)
            VALUES (@t, @role, 1, current_date - 1, 'EFFECTIVE', 'seed', 'test seed', @role, @permissions)
            ON CONFLICT DO NOTHING
            """, ("t", Tenant), ("role", PrivilegedRole), ("permissions", new[] { IntegrationPermissions.DeadLetterRead, IntegrationPermissions.DeadLetterUpdate }));
        await db.ExecuteAsOwner("""
            INSERT INTO iam.cfg_authentication_and_session_policies
              (tenant_id, code, version, effective_from, record_status, created_by, change_reason, policy_code, mfa_required_roles, session_timeout, lockout_attempts)
            VALUES (@t, 'MC_POLICY', 1, current_date - 1, 'EFFECTIVE', 'seed', 'test', 'MC_POLICY', @roles, interval '30 minutes', 5)
            ON CONFLICT DO NOTHING
            """, ("t", Tenant), ("roles", new[] { PrivilegedRole }));

        // The second administrator holds authority for privileged role approvals (WFL-004).
        var approverRole = await db.RoleWithPermissions(Tenant);
        await db.AssignRole(Tenant, second, approverRole);
        await db.ExecuteAsOwner($"""
            INSERT INTO workflow.cfg_delegation_and_approval_authority
              (tenant_id, code, version, effective_from, record_status, created_by, change_reason, role_code, transaction_type, limit_value, delegation_allowed)
            VALUES (@t, @code, 1, current_date - 1, 'EFFECTIVE', 'seed', 'test', @role, '{IdentityApprovals.ApprovalType}', 0, false)
            """, ("t", Tenant), ("code", "AUTH-" + approverRole[..12]), ("role", approverRole));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task PrivilegedRole_IsPendingAndGivesNoPermission_UntilASecondPersonApproves_OQ038()
    {
        var operatorId = await fixture.Database.UserWithPermissions(Tenant, "night-operator");
        var night = _factory.ClientFor(Tenant, "night-operator");
        var adminOne = _factory.ClientFor(Tenant, "admin-one");
        var adminTwo = _factory.ClientFor(Tenant, "admin-two");

        var user = await adminOne.GetAsync($"/api/v1/identity/users/{operatorId}");
        (await adminOne.Send(HttpMethod.Post, $"/api/v1/identity/users/{operatorId}/assign-role", new { roleCode = PrivilegedRole }, ifMatch: user.ETag()))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var assignment = (await (await adminOne.GetAsync($"/api/v1/identity/users/{operatorId}")).Json()).GetProperty("roleAssignments")
            .EnumerateArray().Single(a => a.GetProperty("roleCode").GetString() == PrivilegedRole);
        assignment.GetProperty("status").GetString().ShouldBe("PendingApproval");
        (await night.GetAsync("/api/v1/integration/dead-letters")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        await _factory.DrainOutbox();
        var approval = await Approval(adminOne, IdentityApprovals.BusinessRefType, assignment.GetProperty("id").GetGuid());
        (await (await adminOne.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"")).Code()).ShouldBe("MAKER_CHECKER_SAME_PERSON");
        await _factory.DrainOutbox();
        (await night.GetAsync("/api/v1/integration/dead-letters")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await adminTwo.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await _factory.DrainOutbox();
        (await night.GetAsync("/api/v1/integration/dead-letters")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await fixture.Database.ScalarAsOwner<string>("SELECT status FROM iam.user_role_assignment WHERE user_id = @u AND role_code = @r",
            ("u", operatorId), ("r", PrivilegedRole))).ShouldBe("ACTIVE");
    }

    [Fact]
    public async Task SwitchingTier2Off_NeedsASecondPersonsApproval_OQ046()
    {
        var adminOne = _factory.ClientFor(Tenant, "admin-one");
        var adminTwo = _factory.ClientFor(Tenant, "admin-two");
        const string settingsUrl = "/api/v1/static-data/settings";

        var requested = await adminOne.Send(HttpMethod.Put, $"{settingsUrl}/tier2-maker-checker", new { tier2MakerChecker = false }, ifMatch: "\"0\"");
        requested.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var settings = await (await adminOne.GetAsync(settingsUrl)).Json();
        settings.GetProperty("tier2MakerChecker").GetBoolean().ShouldBeTrue();
        settings.GetProperty("pendingTier2MakerChecker").GetBoolean().ShouldBeFalse();

        await _factory.DrainOutbox();
        var settingsId = await fixture.Database.ScalarAsOwner<Guid>("SELECT id FROM sdm.tenant_governance_setting WHERE tenant_id = @t", ("t", Tenant));
        var approval = await Approval(adminOne, StaticDataApprovals.GovernanceBusinessRefType, settingsId);
        (await (await adminOne.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"")).Code()).ShouldBe("MAKER_CHECKER_SAME_PERSON");
        await _factory.DrainOutbox();
        (await (await adminOne.GetAsync(settingsUrl)).Json()).GetProperty("tier2MakerChecker").GetBoolean().ShouldBeTrue();

        (await adminTwo.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await _factory.DrainOutbox();
        settings = await (await adminOne.GetAsync(settingsUrl)).Json();
        settings.GetProperty("tier2MakerChecker").GetBoolean().ShouldBeFalse();
        settings.GetProperty("pendingTier2MakerChecker").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);

        // Switching it back on takes effect at once.
        (await adminOne.Send(HttpMethod.Put, $"{settingsUrl}/tier2-maker-checker", new { tier2MakerChecker = true }, ifMatch: $"\"{settings.GetProperty("rowVersion").GetInt64()}\""))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await (await adminOne.GetAsync(settingsUrl)).Json()).GetProperty("tier2MakerChecker").GetBoolean().ShouldBeTrue();
    }

    private static async Task<Guid> Approval(HttpClient client, string businessRefType, Guid businessRefId)
    {
        var page = await (await client.GetAsync($"/api/v1/workflow/approvals?status=Requested&businessRefType={businessRefType}&businessRefId={businessRefId}")).Json();
        return page.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid();
    }
}
