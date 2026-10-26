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
using System.Text.Json;
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
    public async Task TwoFirstAdministrators_AreCreatedInTheNewTenant_OnlyAfterASecondPlatformAdministratorApproves_OQ067_BR013()
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

        // A federated identity is linked at once: both administrators are usable, so the request reads Provisioned (BR-018).
        var request = await (await one.GetAsync($"/api/v1/identity/tenant-administrators/{requestId}")).Json();
        DocumentedSchema.ResponseProblems(request, "platform-operator", "GetTenantAdministratorRequest").ShouldBeEmpty();
        (request.GetProperty("status").GetString(), request.GetProperty("kind").GetString()).ShouldBe(("Provisioned", "FirstAdministrators"));
        var people = request.GetProperty("administrators").EnumerateArray().ToList();
        people.Select(p => (p.GetProperty("username").GetString(), p.GetProperty("identityState").GetString())).ShouldBe(
            [("firstadm-admin", "Linked"), ("firstadm-admin-2", "Linked")]);
        var checker = await fixture.Database.ScalarAsOwner<Guid>("SELECT id FROM iam.user_account WHERE tenant_id = @t AND username = 'platform-two'", ("t", Operator));
        foreach (var userId in people.Select(p => p.GetProperty("userId").GetGuid()))
        {
            (await fixture.Database.ScalarAsOwner<string>(
                    "SELECT status || ' ' || approved_by FROM iam.user_role_assignment WHERE tenant_id = @t AND user_id = @u AND role_code = 'TENANT_ADMIN'",
                    ("t", tenantId), ("u", userId)))
                .ShouldBe($"ACTIVE {checker}");
        }

        // OQ-099: the role holds exactly the functions the two platform administrators agreed.
        (await fixture.Database.ScalarAsOwner<string[]>(
                "SELECT array_agg(function_code ORDER BY function_code) FROM iam.cfg_role_function_assignments WHERE tenant_id = @t AND role_code = 'TENANT_ADMIN'",
                ("t", tenantId)))
            .ShouldBe(AdminFunctions);

        // BR-013: the access step waits for both administrators to be active (identity linked, signed in at least once).
        var tenantView = await (await one.GetAsync(tenantUrl)).Json();
        DocumentedSchema.ResponseProblems(tenantView, "platform-operator", "GetTenant").ShouldBeEmpty();
        tenantView.GetProperty("onboardingStep").GetString().ShouldBe("ORGANISATION_AND_ACCESS");
        (await AccessCheck(one, tenantUrl)).ShouldBe(["TENANT_ADMIN: 0 of 2 active"]);
        var admin = _factory.ClientFor(tenantId, "firstadm-admin");
        (await admin.GetAsync("/api/v1/identity/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await AccessCheck(one, tenantUrl)).ShouldBe(["TENANT_ADMIN: 1 of 2 active"]);
        (await _factory.ClientFor(tenantId, "firstadm-admin-2").GetAsync("/api/v1/identity/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await AccessCheck(one, tenantUrl)).ShouldBeEmpty();

        // The tenant's users act while it is onboarding, to load its configuration (decided in Wave 0b batch 6), and once active.
        var tenant = await one.GetAsync(tenantUrl);
        await CompleteOnboardingAndActivate(one, tenantUrl, tenant.ETag(), "ORGANISATION_AND_ACCESS");
        var me = await (await admin.GetAsync("/api/v1/identity/me")).Json();
        var role = me.GetProperty("roles").EnumerateArray().Single();
        (role.GetProperty("roleCode").GetString(), role.GetProperty("status").GetString(), role.GetProperty("inEffect").GetBoolean())
            .ShouldBe(("TENANT_ADMIN", "Active", true));
        (await admin.GetAsync("/api/v1/identity/users")).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Once active, first administrators are no longer provisioned; an additional administrator is asked for instead.
        (await (await one.Send(HttpMethod.Post, "/api/v1/identity/tenant-administrators", Request(tenantId, "late"), idempotent: true)).Code())
            .ShouldBe("TENANT_NOT_ONBOARDING");
    }

    /// <summary>
    /// BR-013: an existing tenant gets one more administrator, asked for by a platform operator and approved by a second
    /// platform administrator, never by anyone inside the tenant.
    /// </summary>
    [Fact]
    public async Task AnAdditionalAdministrator_ForAnExistingTenant_IsApprovedByASecondPlatformAdministrator_BR013()
    {
        var one = _factory.ClientFor(Operator, "platform-one");
        var two = _factory.ClientFor(Operator, "platform-two");
        var tenantId = await ActiveTenantWithAdministrators(one, two, "ADDADM");

        var body = new { tenantId, administrator = Person("addadm-third") };
        var requested = await one.Send(HttpMethod.Post, "/api/v1/identity/tenant-administrators/additional", body, idempotent: true);
        requested.StatusCode.ShouldBe(HttpStatusCode.Created, await requested.Content.ReadAsStringAsync());
        var requestId = (await requested.Json()).GetProperty("id").GetGuid();
        (await (await one.Send(HttpMethod.Post, "/api/v1/identity/tenant-administrators/additional",
            new { tenantId, administrator = Person("addadm-admin") }, idempotent: true)).Code()).ShouldBe("DUPLICATE_RECORD");
        await _factory.DrainOutbox();

        // The approval is the platform's, in the operator tenant: the tenant's own administrators can neither read nor decide it.
        var tenantAdmin = _factory.ClientFor(tenantId, "addadm-admin");
        var approval = await Approval(one, requestId);
        (await tenantAdmin.GetAsync($"/api/v1/workflow/approvals/{approval}")).StatusCode.ShouldNotBe(HttpStatusCode.OK);
        (await tenantAdmin.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"")).StatusCode
            .ShouldNotBe(HttpStatusCode.NoContent);

        (await two.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await _factory.DrainOutbox();

        var request = await (await one.GetAsync($"/api/v1/identity/tenant-administrators/{requestId}")).Json();
        (request.GetProperty("status").GetString(), request.GetProperty("kind").GetString()).ShouldBe(("Provisioned", "AdditionalAdministrator"));
        request.GetProperty("functions").EnumerateArray().ShouldBeEmpty();
        var third = _factory.ClientFor(tenantId, "addadm-third");
        var me = await (await third.GetAsync("/api/v1/identity/me")).Json();
        me.GetProperty("roles").EnumerateArray().Single().GetProperty("roleCode").GetString().ShouldBe("TENANT_ADMIN");
        (await third.GetAsync("/api/v1/identity/users")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await fixture.Database.ScalarAsOwner<long>(
            "SELECT count(*) FROM iam.cfg_roles_and_permission_sets WHERE tenant_id = @t AND role_code = 'TENANT_ADMIN'", ("t", tenantId))).ShouldBe(1, "the existing role");
    }

    [Fact]
    public async Task ARequestNamingOneAdministrator_OrTheSamePersonTwice_IsRefused_BR013()
    {
        var one = _factory.ClientFor(Operator, "platform-one");
        var (tenantId, _) = await RegisterTenantAtAccessStep(one, "ONEADM");
        var single = await one.Send(HttpMethod.Post, "/api/v1/identity/tenant-administrators",
            new { tenantId, administrators = new[] { Person("oneadm-a") }, functions = AdminFunctions }, idempotent: true);
        single.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await single.Json()).GetProperty("errors").TryGetProperty("administrators", out _).ShouldBeTrue();
        (await (await one.Send(HttpMethod.Post, "/api/v1/identity/tenant-administrators",
            new { tenantId, administrators = new[] { Person("oneadm-a"), Person("ONEADM-A") }, functions = AdminFunctions }, idempotent: true)).Code())
            .ShouldBe("VALIDATION_FAILED");
    }

    [Fact]
    public async Task ATenantThatAlreadyHasUsers_GetsNoFirstAdministrators()
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

    /// <summary>
    /// BR-018: administrators of INSYS's own realm wait for their Keycloak users. With Keycloak unreachable (this host has
    /// none) the request reads IdentityParked, with the reason, never Provisioned.
    /// </summary>
    [Fact]
    public async Task AdministratorsOfInsysOwnRealm_GiveAnEmail_AndTheRequestReadsIdentityParked_WhileKeycloakIsDown_BR018()
    {
        var one = _factory.ClientFor(Operator, "platform-one");
        var two = _factory.ClientFor(Operator, "platform-two");
        var (tenantId, _) = await RegisterTenantAtAccessStep(one, "OWNREALM");
        var body = new { tenantId, administrators = new[] { OwnRealm("own-admin"), OwnRealm("own-admin-2") }, functions = new[] { "IDENTITY_USERS" } };
        var requested = await one.Send(HttpMethod.Post, "/api/v1/identity/tenant-administrators", body, idempotent: true);
        requested.StatusCode.ShouldBe(HttpStatusCode.Created, await requested.Content.ReadAsStringAsync());
        var requestId = (await requested.Json()).GetProperty("id").GetGuid();
        await _factory.DrainOutbox();
        (await two.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{await Approval(one, requestId)}/approve", ifMatch: "\"1\"")).StatusCode
            .ShouldBe(HttpStatusCode.NoContent);

        await _factory.DrainOutbox();

        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM iam.user_account WHERE tenant_id = @t AND status = 'PENDING_IDENTITY' AND external_subject IS NULL",
            ("t", tenantId))).ShouldBe(2);
        var request = await (await one.GetAsync($"/api/v1/identity/tenant-administrators/{requestId}")).Json();
        request.GetProperty("status").GetString().ShouldBe("IdentityParked");
        request.GetProperty("administrators").EnumerateArray().Select(p => (p.GetProperty("identityState").GetString(), p.GetProperty("identityReason").GetString()))
            .ShouldAllBe(p => p.Item1 == "Parked" && p.Item2!.StartsWith("KEYCLOAK_UNAVAILABLE"));
    }

    /// <summary>BR-012: a tenant's own staff read its onboarding readiness (their Home shows what is missing), and only their own tenant's.</summary>
    [Fact]
    public async Task ATenantsOwnStaff_ReadItsOnboardingReadiness_BR012()
    {
        var one = _factory.ClientFor(Operator, "platform-one");
        var (tenantId, _) = await RegisterTenantAtAccessStep(one, "OWNREADY");
        await fixture.Database.UserWithPermissions(tenantId, "ownready-reader", TenancyPermissions.Read);

        var response = await _factory.ClientFor(tenantId, "ownready-reader").GetAsync("/api/v1/tenancy/readiness");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var readiness = await response.Json();
        DocumentedSchema.ResponseProblems(readiness, "core", "GetOwnTenantReadiness").ShouldBeEmpty();
        readiness.GetProperty("tenantId").GetGuid().ShouldBe(tenantId);
        readiness.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("step").GetString() == "ORGANISATION_AND_ACCESS")
            .GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("code").GetString() == "FIRST_ADMINISTRATOR")
            .GetProperty("missing").EnumerateArray().Select(m => m.GetString()).ShouldBe(["TENANT_ADMIN: 0 of 2 active"]);

        // BR-022: /identity/me gives the locale to format with: this request's language and the tenant's country, and its currency.
        var locale = (await (await _factory.ClientFor(tenantId, "ownready-reader").GetAsync("/api/v1/identity/me")).Json()).GetProperty("locale");
        (locale.GetProperty("tag").GetString(), locale.GetProperty("country").GetString(), locale.GetProperty("baseCurrency").GetString()).ShouldBe(("en-ZA", "ZA", "ZAR"));

        // Another tenant's user reads its own tenant's, never this one's.
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "tenant-a-ready-reader", TenancyPermissions.Read);
        (await (await _factory.ClientFor(DatabaseFixture.TenantA, "tenant-a-ready-reader").GetAsync("/api/v1/tenancy/readiness")).Json())
            .GetProperty("tenantId").GetGuid().ShouldBe(DatabaseFixture.TenantA);
    }

    [Fact]
    public async Task ARequestNamingAFunctionNotForStaff_IsRefused_OQ099()
    {
        var one = _factory.ClientFor(Operator, "platform-one");
        var (tenantId, _) = await RegisterTenantAtAccessStep(one, "BADFN");
        var body = new
        {
            tenantId, administrators = new[] { Person("bad-fn"), Person("bad-fn-2") },
            functions = new[] { "IDENTITY_USERS", "BROKER_PORTAL_BILLING_INVOICES", "NOT_A_FUNCTION" },
        };

        var refused = await one.Send(HttpMethod.Post, "/api/v1/identity/tenant-administrators", body, idempotent: true);

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await refused.Json()).GetProperty("errors").GetProperty("functions")[0].GetString()
            .ShouldBe("Not a staff function in force: BROKER_PORTAL_BILLING_INVOICES, NOT_A_FUNCTION.");
    }

    // OQ-099: the request names functions: users (read), provisioning (create), role assignment (update) and dead letters (read).
    private static readonly string[] AdminFunctions = ["ASSIGN_ROLE", "IDENTITY_USERS", "INTEGRATION_DEAD_LETTERS", "PROVISION_USER"];

    /// <summary>Two federated administrators: the first named <paramref name="username"/>, the second with "-2".</summary>
    private static object Request(Guid tenantId, string username) => new
    {
        tenantId, administrators = new[] { Person(username), Person(username + "-2") }, functions = AdminFunctions,
    };

    private static object Person(string username) => new
    {
        username, displayName = "Administrator " + username, idpCode = "OIDC", externalSubject = username, mfaEnrolled = true,
    };

    private static object OwnRealm(string username) => new
    {
        username, displayName = "Own Administrator", idpCode = "KEYCLOAK", email = $"{username}@example.com", mfaEnrolled = true,
    };

    /// <summary>The ORGANISATION_AND_ACCESS step's administrator check: what is missing.</summary>
    private static async Task<List<string>> AccessCheck(HttpClient client, string tenantUrl)
    {
        var readiness = await (await client.GetAsync($"{tenantUrl}/readiness")).Json();
        return [.. readiness.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("step").GetString() == "ORGANISATION_AND_ACCESS")
            .GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("code").GetString() == "FIRST_ADMINISTRATOR")
            .GetProperty("missing").EnumerateArray().Select(m => m.GetString()!)];
    }

    /// <summary>A tenant with its two first administrators, both signed in, onboarded and active.</summary>
    private async Task<Guid> ActiveTenantWithAdministrators(HttpClient one, HttpClient two, string code)
    {
        var (tenantId, url) = await RegisterTenantAtAccessStep(one, code);
        var prefix = code.ToLowerInvariant() + "-admin";
        var requestId = (await (await one.Send(HttpMethod.Post, "/api/v1/identity/tenant-administrators", Request(tenantId, prefix), idempotent: true)).Json())
            .GetProperty("id").GetGuid();
        await _factory.DrainOutbox();
        (await two.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{await Approval(one, requestId)}/approve", ifMatch: "\"1\"")).StatusCode
            .ShouldBe(HttpStatusCode.NoContent);
        await _factory.DrainOutbox();
        foreach (var admin in new[] { prefix, prefix + "-2" })
        {
            (await _factory.ClientFor(tenantId, admin).GetAsync("/api/v1/identity/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await CompleteOnboardingAndActivate(one, url, (await one.GetAsync(url)).ETag(), "ORGANISATION_AND_ACCESS");
        return tenantId;
    }

    private async Task<(Guid TenantId, string Url)> RegisterTenantAtAccessStep(HttpClient client, string code)
    {
        var registered = await client.Send(HttpMethod.Post, "/api/v1/tenancy/tenants", new { tenantCode = code, legalName = code + " Ltd", isolationProfile = "B", timeZone = "Africa/Johannesburg" },
            idempotent: true);
        var tenantId = (await registered.Json()).GetProperty("id").GetGuid();
        var url = $"/api/v1/tenancy/tenants/{tenantId}";
        var etag = (await client.GetAsync(url)).ETag();
        await fixture.Database.SeedReadyTenant(tenantId, TestClock.Today.AddDays(-30));
        foreach (var step in new[] { "PLATFORM_REFERENCE_DATA", "TENANT_REGISTRATION" })
        {
            var completed = await client.Send(HttpMethod.Post, $"{url}/complete-onboarding-step", new { step }, ifMatch: etag);
            completed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
            etag = completed.ETag();
        }

        return (tenantId, url);
    }

    private async Task CompleteOnboardingAndActivate(HttpClient client, string url, string etag, string? firstStep = null)
    {
        if (firstStep is not null)
        {
            var completed = await client.Send(HttpMethod.Post, $"{url}/complete-onboarding-step", new { step = firstStep }, ifMatch: etag);
            completed.StatusCode.ShouldBe(HttpStatusCode.NoContent, await completed.Content.ReadAsStringAsync());
            etag = completed.ETag();
        }

        etag = await client.CompleteOnboarding(_factory.ClientFor(Operator, "platform-two"), url, etag);
        (await client.Send(HttpMethod.Post, $"{url}/activate", new { reason = "Go live" }, ifMatch: etag)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    private static async Task<Guid> Approval(HttpClient client, Guid requestId)
    {
        var page = await (await client.GetAsync(
            $"/api/v1/workflow/approvals?status=Requested&businessRefType={IdentityApprovals.TenantAdministratorBusinessRefType}&businessRefId={requestId}")).Json();
        return page.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid();
    }
}
