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
using Insys.Identity.Contracts;
using Insys.Integration.Contracts;
using Insys.Tenancy.Contracts;
using Insys.Testing;
using Insys.Workflow.Contracts;

namespace Insys.IntegrationTests;

/// <summary>
/// A new tenant's first administrator (OQ-067, GAP-019): requested by one platform administrator while the tenant is
/// Onboarding, approved by a second in Workflow (SRS-002), then created in the new tenant with its TENANT_ADMIN role.
/// </summary>
public sealed class TenantAdministratorTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private static readonly Guid Operator = DatabaseFixture.Operator;
    private static readonly string[] PlatformPermissions =
    [
        TenancyPermissions.Read, TenancyPermissions.Create, TenancyPermissions.Update, IdentityPermissions.UserRead, IdentityPermissions.UserCreate,
        WorkflowPermissions.ApprovalRead, WorkflowPermissions.ApprovalApprove,
    ];

    private InsysApiFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = await fixture.Host();
        var db = fixture.Database;
        await db.UserWithPermissions(Operator, "platform-one", PlatformPermissions);
        var second = await db.UserWithPermissions(Operator, "platform-two", PlatformPermissions);

        // The second platform administrator holds authority for first-administrator approvals (WFL-004).
        var approverRole = await db.RoleWithPermissions(Operator);
        await db.AssignRole(Operator, second, approverRole);
        await db.ExecuteAsOwner($"""
            INSERT INTO workflow.cfg_delegation_and_approval_authority
              (tenant_id, code, version, effective_from, record_status, created_by, change_reason, role_code, transaction_type, limit_value, delegation_allowed)
            VALUES (@t, @code, 1, current_date - 1, 'EFFECTIVE', 'seed', 'test', @role, '{IdentityApprovals.TenantAdministratorApprovalType}', 0, false)
            """, ("t", Operator), ("code", "AUTH-" + approverRole[..12]), ("role", approverRole));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task FirstAdministrator_IsCreatedInTheNewTenant_OnlyAfterASecondPlatformAdministratorApproves_OQ067()
    {
        var one = _factory.ClientFor(Operator, "platform-one");
        var two = _factory.ClientFor(Operator, "platform-two");
        var (tenantId, tenantUrl) = await RegisterTenantAtAccessStep(one, "FIRSTADM");

        var requested = await one.Send(HttpMethod.Post, "/api/v1/identity/tenant-administrators", Request(tenantId, "firstadm-admin"), idempotent: true);
        requested.StatusCode.ShouldBe(HttpStatusCode.Created, await requested.Content.ReadAsStringAsync());
        var requestId = (await requested.Json()).GetProperty("id").GetGuid();
        (await (await one.Send(HttpMethod.Post, "/api/v1/identity/tenant-administrators", Request(tenantId, "another"), idempotent: true)).Code())
            .ShouldBe("DUPLICATE_RECORD");

        // Nothing exists in the new tenant until the second platform administrator approves.
        await _factory.DrainOutbox();
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM iam.user_account WHERE tenant_id = @t", ("t", tenantId))).ShouldBe(0);
        var approval = await Approval(one, requestId);
        (await (await one.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"")).Code()).ShouldBe("MAKER_CHECKER_SAME_PERSON");

        (await two.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await _factory.DrainOutbox();

        var request = await (await one.GetAsync($"/api/v1/identity/tenant-administrators/{requestId}")).Json();
        DocumentedSchema.ResponseProblems(request, "platform-operator", "GetTenantAdministratorRequest").ShouldBeEmpty();
        request.GetProperty("status").GetString().ShouldBe("Provisioned");
        var userId = request.GetProperty("userId").GetGuid();
        var checker = await fixture.Database.ScalarAsOwner<Guid>("SELECT id FROM iam.user_account WHERE tenant_id = @t AND username = 'platform-two'", ("t", Operator));
        (await fixture.Database.ScalarAsOwner<string>(
                "SELECT status || ' ' || approved_by FROM iam.user_role_assignment WHERE tenant_id = @t AND user_id = @u AND role_code = 'TENANT_ADMIN'",
                ("t", tenantId), ("u", userId)))
            .ShouldBe($"ACTIVE {checker}");
        (await fixture.Database.ScalarAsOwner<string[]>(
                "SELECT permissions FROM iam.cfg_roles_and_permission_sets WHERE tenant_id = @t AND role_code = 'TENANT_ADMIN'", ("t", tenantId)))
            .ShouldBe([IntegrationPermissions.DeadLetterRead, IdentityPermissions.UserCreate, IdentityPermissions.UserRead, IdentityPermissions.UserUpdate],
                ignoreOrder: true);

        // Tenancy completes the access step: the next one is current.
        var tenant = await one.GetAsync(tenantUrl);
        var tenantView = await tenant.Json();
        DocumentedSchema.ResponseProblems(tenantView, "platform-operator", "GetTenant").ShouldBeEmpty();
        tenantView.GetProperty("onboardingStep").GetString().ShouldBe("FINANCIAL_FOUNDATIONS");

        // The tenant's users act once it is active (TEN-008); then the administrator administers its users.
        var admin = _factory.ClientFor(tenantId, "firstadm-admin");
        (await admin.GetAsync("/api/v1/identity/me")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await CompleteOnboardingAndActivate(one, tenantUrl, tenant.ETag());
        _factory.ResetState(); // the permission cache holds the refused principal for up to its duration (30 seconds, ADR-006)
        var me = await admin.GetAsync("/api/v1/identity/me");
        me.StatusCode.ShouldBe(HttpStatusCode.OK, await me.Content.ReadAsStringAsync());
        var body = await me.Json();
        body.GetProperty("userId").GetGuid().ShouldBe(userId);
        var role = body.GetProperty("roles").EnumerateArray().Single();
        (role.GetProperty("roleCode").GetString(), role.GetProperty("status").GetString(), role.GetProperty("inEffect").GetBoolean())
            .ShouldBe(("TENANT_ADMIN", "Active", true));
        (await admin.GetAsync("/api/v1/identity/users")).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Once active, the tenant's own administrators add users; the platform no longer does.
        (await (await one.Send(HttpMethod.Post, "/api/v1/identity/tenant-administrators", Request(tenantId, "late"), idempotent: true)).Code())
            .ShouldBe("TENANT_NOT_ONBOARDING");
    }

    [Fact]
    public async Task ATenantThatAlreadyHasUsers_GetsNoFirstAdministrator()
    {
        var one = _factory.ClientFor(Operator, "platform-one");
        var (tenantId, _) = await RegisterTenantAtAccessStep(one, "HASUSERS");
        await fixture.Database.UserWithPermissions(tenantId, "hasusers-someone");

        (await (await one.Send(HttpMethod.Post, "/api/v1/identity/tenant-administrators", Request(tenantId, "hasusers-admin"), idempotent: true)).Code())
            .ShouldBe("TENANT_HAS_USERS");
    }

    [Fact]
    public async Task ARejectedRequest_CreatesNothing()
    {
        var one = _factory.ClientFor(Operator, "platform-one");
        var two = _factory.ClientFor(Operator, "platform-two");
        var (tenantId, _) = await RegisterTenantAtAccessStep(one, "REJADM");
        var requestId = (await (await one.Send(HttpMethod.Post, "/api/v1/identity/tenant-administrators", Request(tenantId, "rejadm-admin"), idempotent: true))
            .Json()).GetProperty("id").GetGuid();
        await _factory.DrainOutbox();

        (await two.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{await Approval(one, requestId)}/reject", new { reason = "Wrong person" }, ifMatch: "\"1\""))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await _factory.DrainOutbox();

        (await (await one.GetAsync($"/api/v1/identity/tenant-administrators/{requestId}")).Json()).GetProperty("status").GetString().ShouldBe("Rejected");
        (await _factory.ClientFor(tenantId, "rejadm-admin").GetAsync("/api/v1/identity/me")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM iam.user_account WHERE tenant_id = @t", ("t", tenantId))).ShouldBe(0);
    }

    [Fact]
    public async Task OnlyThePlatformOperator_AndOnlyForATenantInOnboarding()
    {
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "tenant-a-user-admin", IdentityPermissions.UserCreate);
        (await _factory.ClientFor(DatabaseFixture.TenantA, "tenant-a-user-admin")
                .Send(HttpMethod.Post, "/api/v1/identity/tenant-administrators", Request(DatabaseFixture.TenantB, "intruder"), idempotent: true))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // The fixture's tenants are Active.
        var refused = await _factory.ClientFor(Operator, "platform-one")
            .Send(HttpMethod.Post, "/api/v1/identity/tenant-administrators", Request(DatabaseFixture.TenantB, "late-admin"), idempotent: true);
        (await refused.Code()).ShouldBe("TENANT_NOT_ONBOARDING");
    }

    [Fact]
    public async Task AFirstAdministratorOfInsysOwnRealm_GivesAnEmail_AndWaitsForItsKeycloakUser_KeycloakProvisioning()
    {
        var one = _factory.ClientFor(Operator, "platform-one");
        var two = _factory.ClientFor(Operator, "platform-two");
        var (tenantId, _) = await RegisterTenantAtAccessStep(one, "OWNREALM");
        var body = new
        {
            tenantId, username = "own-admin", displayName = "Own Administrator", idpCode = "KEYCLOAK", email = "own.admin@example.com", mfaEnrolled = true,
            permissions = new[] { IdentityPermissions.UserRead },
        };
        var requested = await one.Send(HttpMethod.Post, "/api/v1/identity/tenant-administrators", body, idempotent: true);
        requested.StatusCode.ShouldBe(HttpStatusCode.Created, await requested.Content.ReadAsStringAsync());
        await _factory.DrainOutbox();
        var requestId = await fixture.Database.ScalarAsOwner<Guid>("SELECT id FROM iam.tenant_administrator_request WHERE target_tenant_id = @t", ("t", tenantId));
        (await two.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{await Approval(one, requestId)}/approve", ifMatch: "\"1\"")).StatusCode
            .ShouldBe(HttpStatusCode.NoContent);
        await _factory.DrainOutbox();

        // This host has no Keycloak: the user waits, unable to sign in, and its creation is parked to be retried.
        (await fixture.Database.ScalarAsOwner<string>("SELECT status || ' ' || coalesce(external_subject, '-') || ' ' || email FROM iam.user_account WHERE tenant_id = @t",
            ("t", tenantId))).ShouldBe("PENDING_IDENTITY - own.admin@example.com");
        (await fixture.Database.ScalarAsOwner<string>(
                "SELECT prerequisite_code FROM integration.parked_delivery WHERE tenant_id = @t AND consumer = 'iam.KeycloakProvisioning.UserIdentityRequested'", ("t", tenantId)))
            .ShouldBe("KEYCLOAK_UNAVAILABLE");
    }

    private static object Request(Guid tenantId, string username) => new
    {
        tenantId,
        username,
        displayName = "First administrator",
        idpCode = "OIDC",
        externalSubject = username,
        mfaEnrolled = true,
        permissions = new[] { IdentityPermissions.UserRead, IdentityPermissions.UserCreate, IdentityPermissions.UserUpdate, IntegrationPermissions.DeadLetterRead },
    };

    private async Task<(Guid TenantId, string Url)> RegisterTenantAtAccessStep(HttpClient client, string code)
    {
        var registered = await client.Send(HttpMethod.Post, "/api/v1/tenancy/tenants", new { tenantCode = code, legalName = code + " Ltd", isolationProfile = "B", timeZone = "Africa/Johannesburg" },
            idempotent: true);
        var tenantId = (await registered.Json()).GetProperty("id").GetGuid();
        var url = $"/api/v1/tenancy/tenants/{tenantId}";
        var etag = (await client.GetAsync(url)).ETag();
        await fixture.Database.SeedReadyTenant(tenantId, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-30));
        foreach (var step in new[] { "PLATFORM_REFERENCE_DATA", "TENANT_REGISTRATION" })
        {
            var completed = await client.Send(HttpMethod.Post, $"{url}/complete-onboarding-step", new { step }, ifMatch: etag);
            completed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
            etag = completed.ETag();
        }

        return (tenantId, url);
    }

    private static async Task CompleteOnboardingAndActivate(HttpClient client, string url, string etag)
    {
        var tenant = await (await client.GetAsync(url)).Json();
        var steps = tenant.GetProperty("onboardingSteps").EnumerateArray().Select(s => s.GetString()!).ToList();
        foreach (var step in steps.SkipWhile(s => s != tenant.GetProperty("onboardingStep").GetString()))
        {
            var completed = await client.Send(HttpMethod.Post, $"{url}/complete-onboarding-step", OnboardingSeed.StepBody(step), ifMatch: etag);
            completed.StatusCode.ShouldBe(HttpStatusCode.NoContent, await completed.Content.ReadAsStringAsync());
            etag = completed.ETag();
        }

        (await client.Send(HttpMethod.Post, $"{url}/activate", new { reason = "Go live" }, ifMatch: etag)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    private static async Task<Guid> Approval(HttpClient client, Guid requestId)
    {
        var page = await (await client.GetAsync(
            $"/api/v1/workflow/approvals?status=Requested&businessRefType={IdentityApprovals.TenantAdministratorBusinessRefType}&businessRefId={requestId}")).Json();
        return page.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid();
    }
}
