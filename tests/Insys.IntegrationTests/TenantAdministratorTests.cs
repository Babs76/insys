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
using Insys.Seed;
using Insys.StaticData.Contracts;
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
            VALUES (@t, @code, 1, DATE '2026-01-01', 'EFFECTIVE', 'seed', 'test', @role, '{IdentityApprovals.TenantAdministratorApprovalType}', 0, false)
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

    /// <summary>
    /// BR-013 end to end, with the privileged flags on: a tenant is created, configured and activated, and every approval
    /// inside it is decided by its own people. Its two administrators create a role (a Tier 1 static data change), grant it
    /// a privileged function (another), provision a user and assign them the role (a privileged assignment), each approved
    /// by the other administrator; then a third administrator is added, approved by a second platform administrator.
    /// </summary>
    [Fact]
    public async Task ATenant_IsCreatedConfiguredAndActivated_UsingOnlyItsOwnPeople_WithThePrivilegedFlagsOn_BR013()
    {
        var one = _factory.ClientFor(Operator, "platform-one");
        var two = _factory.ClientFor(Operator, "platform-two");
        (await fixture.Database.ScalarAsOwner<string>("""
            SELECT string_agg(function_code || '=' || privileged, ',' ORDER BY function_code) FROM iam.ref_function_catalogue
            WHERE tenant_id IS NULL AND function_code IN ('ASSIGN_ROLE', 'DISCARD_MESSAGE', 'SD_IDENTITY_AND_ACCESS_MANAGEMENT_APPROVE')
              AND record_status = 'ACTIVE' AND effective_to IS NULL
            """)).ShouldBe("ASSIGN_ROLE=true,DISCARD_MESSAGE=true,SD_IDENTITY_AND_ACCESS_MANAGEMENT_APPROVE=true");

        // The platform: register the tenant and ask for its two administrators; a second platform administrator approves.
        var (tenantId, url) = await RegisterTenantAtAccessStep(one, "OWNPEOPLE");
        var request = new { tenantId, administrators = new[] { Person("ownpeople-ana"), Person("ownpeople-ben") }, functions = ConfiguringFunctions };
        var requestId = (await (await one.Send(HttpMethod.Post, "/api/v1/identity/tenant-administrators", request, idempotent: true)).Json()).GetProperty("id").GetGuid();
        await _factory.DrainOutbox();
        (await two.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{await Approval(one, requestId)}/approve", ifMatch: "\"1\"")).StatusCode
            .ShouldBe(HttpStatusCode.NoContent);
        await _factory.DrainOutbox();
        // The administrators' approval authority is in force in the tenant with them (role-templates.md, OQ-119).
        (await fixture.Database.ScalarAsOwner<string>("""
            SELECT string_agg(transaction_type, ',' ORDER BY transaction_type) FROM workflow.cfg_delegation_and_approval_authority
            WHERE tenant_id = @t AND role_code = 'TENANT_ADMIN' AND record_status = 'PUBLISHED'
            """, ("t", tenantId))).ShouldBe("EXTERNAL_SUBJECT_LINK,PRIVILEGED_ROLE,STATIC_DATA_T1,STATIC_DATA_T2",
            await fixture.Database.ScalarAsOwner<string>("SELECT string_agg(consumer || ' ' || status || ' ' || coalesce(prerequisite_code, ''), '; ') FROM integration.parked_delivery WHERE consumer LIKE '%Authority%'"));
        var ana = _factory.ClientFor(tenantId, "ownpeople-ana");
        var ben = _factory.ClientFor(tenantId, "ownpeople-ben");

        // The tenant's administrators: a new role, made by one and approved by the other.
        const string roles = "/api/v1/identity/static/roles-and-permission-sets";
        var role = await ana.Send(HttpMethod.Post, roles, new { code = "OPS_LEAD", roleCode = "OPS_LEAD", changeReason = "Operations lead" }, idempotent: true);
        role.StatusCode.ShouldBe(HttpStatusCode.Created, await role.Content.ReadAsStringAsync());
        (await ana.Send(HttpMethod.Post, $"{roles}/OPS_LEAD/versions/1/submit", ifMatch: role.ETag())).StatusCode.ShouldBe(HttpStatusCode.OK);
        await ApproveInTenant(ana, ben, tenantId, StaticDataApprovals.BusinessRefType, "OPS_LEAD@1");
        await Activate(ana, $"{roles}/OPS_LEAD");

        // A privileged function granted to it: Tier 1 again, and it makes the role privileged.
        const string grants = "/api/v1/identity/static/role-function-assignments";
        var grant = await ana.Send(HttpMethod.Post, grants, new { code = "OPS_LEAD:DISCARD_MESSAGE", roleCode = "OPS_LEAD", functionCode = "DISCARD_MESSAGE",
            changeReason = "Operations lead discards dead letters" }, idempotent: true);
        grant.StatusCode.ShouldBe(HttpStatusCode.Created, await grant.Content.ReadAsStringAsync());
        (await ana.Send(HttpMethod.Post, $"{grants}/OPS_LEAD:DISCARD_MESSAGE/versions/1/submit", ifMatch: grant.ETag())).StatusCode.ShouldBe(HttpStatusCode.OK);
        await ApproveInTenant(ana, ben, tenantId, StaticDataApprovals.BusinessRefType, "OPS_LEAD:DISCARD_MESSAGE@1");
        await Activate(ana, $"{grants}/OPS_LEAD:DISCARD_MESSAGE");

        // A user, given the privileged role: pending until the other administrator approves (SRS-002).
        var provisioned = await ana.Send(HttpMethod.Post, "/api/v1/identity/users", new
        {
            username = "ownpeople-ops", displayName = "Operations Lead", idpCode = "OIDC", externalSubject = "ownpeople-ops", mfaEnrolled = true,
        }, idempotent: true);
        provisioned.StatusCode.ShouldBe(HttpStatusCode.Created, await provisioned.Content.ReadAsStringAsync());
        var opsId = (await provisioned.Json()).GetProperty("id").GetGuid();
        var ops = await ana.GetAsync($"/api/v1/identity/users/{opsId}");
        var assigned = await ana.Send(HttpMethod.Post, $"/api/v1/identity/users/{opsId}/assign-role", new { roleCode = "OPS_LEAD" }, ifMatch: ops.ETag());
        assigned.StatusCode.ShouldBe(HttpStatusCode.NoContent, await assigned.Content.ReadAsStringAsync());
        var assignment = (await (await ana.GetAsync($"/api/v1/identity/users/{opsId}")).Json()).GetProperty("roleAssignments").EnumerateArray().Single();
        (assignment.GetProperty("status").GetString(), assignment.GetProperty("privileged").GetBoolean()).ShouldBe(("PendingApproval", true));
        await ApproveInTenant(ana, ben, tenantId, IdentityApprovals.BusinessRefType, assignment.GetProperty("id").GetGuid().ToString());
        (await fixture.Database.ScalarAsOwner<string>("SELECT status FROM iam.user_role_assignment WHERE tenant_id = @t AND user_id = @u",
            ("t", tenantId), ("u", opsId))).ShouldBe("ACTIVE");

        // Activation: both administrators are active, the steps complete, and the tenant goes live.
        (await AccessCheck(one, url)).ShouldBeEmpty();
        await CompleteOnboardingAndActivate(one, url, (await one.GetAsync(url)).ETag(), "ORGANISATION_AND_ACCESS");
        (await (await one.GetAsync(url)).Json()).GetProperty("status").GetString().ShouldBe("Active");

        // A third administrator: asked for by the platform, approved by a second platform administrator, never inside the tenant.
        var additional = await one.Send(HttpMethod.Post, "/api/v1/identity/tenant-administrators/additional",
            new { tenantId, administrator = Person("ownpeople-cara") }, idempotent: true);
        additional.StatusCode.ShouldBe(HttpStatusCode.Created, await additional.Content.ReadAsStringAsync());
        var additionalId = (await additional.Json()).GetProperty("id").GetGuid();
        await _factory.DrainOutbox();
        (await two.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{await Approval(one, additionalId)}/approve", ifMatch: "\"1\"")).StatusCode
            .ShouldBe(HttpStatusCode.NoContent);
        await _factory.DrainOutbox();
        (await _factory.ClientFor(tenantId, "ownpeople-cara").GetAsync("/api/v1/identity/users")).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Every approval decided inside the tenant was decided by one of its own people.
        (await fixture.Database.ScalarAsOwner<string>("""
            SELECT count(*) || ' decided, ' || count(*) FILTER (WHERE NOT EXISTS (
                SELECT 1 FROM iam.user_account u WHERE u.tenant_id = a.tenant_id AND u.id = a.checker_id)) || ' by outsiders'
            FROM workflow.approval a WHERE a.tenant_id = @t AND a.status = 'APPROVED'
            """, ("t", tenantId))).ShouldBe("3 decided, 0 by outsiders");
    }

    /// <summary>
    /// What a tenant's first administrators need to configure it themselves: users and roles, the role tables (privileged),
    /// change requests, and approving.
    /// </summary>
    /// <summary>
    /// SRS-002 (Ade, 20 November 2026, OQ-197): the role-templates addendum's three BLOCK rules are a platform baseline, in force with none of the tenant's rows:
    /// a grant giving a role holding CLAIMS_DRAFT_INSTRUCTION CLAIMS_RELEASE_INSTRUCTION too is refused at submission (alone and as two lines of one import),
    /// and one user given a draft role and then a release role is refused; the tenant's own rows on the same pairs change nothing. A grant giving role X one
    /// side, when a user holding X holds the other through role Y, is refused too.
    /// </summary>
    [Fact]
    public async Task ARoleOrAUser_HoldingBothSidesOfABlockRuleInForce_IsRefused_SRS002()
    {
        var one = _factory.ClientFor(Operator, "platform-one");
        var two = _factory.ClientFor(Operator, "platform-two");
        var (tenantId, url) = await RegisterTenantAtAccessStep(one, "SODBLOCK");
        var request = new { tenantId, administrators = new[] { Person("sodblock-ana"), Person("sodblock-ben") }, functions = ConfiguringFunctions };
        var requestId = (await (await one.Send(HttpMethod.Post, "/api/v1/identity/tenant-administrators", request, idempotent: true)).Json()).GetProperty("id").GetGuid();
        await _factory.DrainOutbox();
        (await two.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{await Approval(one, requestId)}/approve", ifMatch: "\"1\"")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await _factory.DrainOutbox();
        var ana = _factory.ClientFor(tenantId, "sodblock-ana");
        var ben = _factory.ClientFor(tenantId, "sodblock-ben");
        foreach (var admin in new[] { ana, ben })
        {
            (await admin.GetAsync("/api/v1/identity/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await CompleteOnboardingAndActivate(one, url, (await one.GetAsync(url)).ETag(), "ORGANISATION_AND_ACCESS");
        const string roles = "/api/v1/identity/static/roles-and-permission-sets";
        const string grants = "/api/v1/identity/static/role-function-assignments";

        async Task NewRole(string code)
        {
            var role = await ana.Send(HttpMethod.Post, roles, new { code, roleCode = code, changeReason = "SRS-002" }, idempotent: true);
            role.StatusCode.ShouldBe(HttpStatusCode.Created, await role.Content.ReadAsStringAsync());
            (await ana.Send(HttpMethod.Post, $"{roles}/{code}/versions/1/submit", ifMatch: role.ETag())).StatusCode.ShouldBe(HttpStatusCode.OK);
            await ApproveInTenant(ana, ben, tenantId, StaticDataApprovals.BusinessRefType, $"{code}@1");
            await Activate(ana, $"{roles}/{code}");
        }

        async Task<HttpResponseMessage> SubmitGrant(string role, string function)
        {
            var created = await ana.Send(HttpMethod.Post, grants, new { code = $"{role}:{function}", roleCode = role, functionCode = function, changeReason = "SRS-002" }, idempotent: true);
            created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
            return await ana.Send(HttpMethod.Post, $"{grants}/{role}:{function}/versions/1/submit", ifMatch: created.ETag());
        }

        async Task Grant(string role, string function)
        {
            (await SubmitGrant(role, function)).StatusCode.ShouldBe(HttpStatusCode.OK);
            await ApproveInTenant(ana, ben, tenantId, StaticDataApprovals.BusinessRefType, $"{role}:{function}@1");
            await Activate(ana, $"{grants}/{role}:{function}");
        }

        async Task<Guid> NewUser(string sub) =>
            (await (await ana.Send(HttpMethod.Post, "/api/v1/identity/users", new { username = sub, displayName = sub, idpCode = "OIDC", externalSubject = sub, mfaEnrolled = true },
                idempotent: true)).Json()).GetProperty("id").GetGuid();

        async Task<HttpResponseMessage> Assign(Guid user, string role)
        {
            var current = await ana.GetAsync($"/api/v1/identity/users/{user}");
            var assigned = await ana.Send(HttpMethod.Post, $"/api/v1/identity/users/{user}/assign-role", new { roleCode = role }, ifMatch: current.ETag());
            await _factory.DrainOutbox();
            return assigned;
        }

        foreach (var role in new[] { "SOD_DRAFTER", "SOD_RELEASER", "SOD_UNRULED", "SOD_OTHER" })
        {
            await NewRole(role);
        }

        await Grant("SOD_DRAFTER", "CLAIMS_DRAFT_INSTRUCTION");
        await Grant("SOD_RELEASER", "CLAIMS_RELEASE_INSTRUCTION");
        await Grant("SOD_UNRULED", "CLAIMS_DRAFT_INSTRUCTION");
        await Grant("SOD_OTHER", "CLAIMS_SUBMIT_INSTRUCTION");

        // None of the tenant's rows: the platform baseline is in force all the same, so both are refused.
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM iam.cfg_segregation_of_duties_conflict_rules WHERE tenant_id = @t", ("t", tenantId)))
            .ShouldBe(0L);
        var baselineGrant = await SubmitGrant("SOD_UNRULED", "CLAIMS_RELEASE_INSTRUCTION");
        baselineGrant.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await baselineGrant.Content.ReadAsStringAsync());
        (await baselineGrant.Code()).ShouldBe("ROLE_SEGREGATION_OF_DUTIES");
        var before = await NewUser("sod-before");
        (await Assign(before, "SOD_DRAFTER")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await (await Assign(before, "SOD_RELEASER")).Code()).ShouldBe("SEGREGATION_OF_DUTIES", "the baseline is in force with no row of the tenant's");

        // Across users: sod-before holds SOD_DRAFTER (the draft side) and SOD_OTHER; giving SOD_OTHER the release side would give them both.
        (await Assign(before, "SOD_OTHER")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var crossing = await SubmitGrant("SOD_OTHER", "CLAIMS_RELEASE_INSTRUCTION");
        crossing.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await crossing.Content.ReadAsStringAsync());
        (await crossing.Code()).ShouldBe("ROLE_SEGREGATION_OF_DUTIES");
        (await crossing.Content.ReadAsStringAsync()).ShouldContain("through role SOD_DRAFTER");

        // The addendum's three rules, in force in the tenant as well (as the OFTZA seed puts them): nothing changes.
        foreach (var (code, a, b) in OftzaSeed.SegregationOfDuties)
        {
            await fixture.Database.ExecuteAsOwner("""
                INSERT INTO iam.cfg_segregation_of_duties_conflict_rules
                  (tenant_id, code, version, effective_from, record_status, created_by, change_reason, function_a, function_b, conflict)
                VALUES (@t, @c, 1, DATE '2026-01-01', 'PUBLISHED', 'seed', 'SRS-002', @a, @b, 'BLOCK')
                """, ("t", tenantId), ("c", code), ("a", a), ("b", b));
        }

        // The role grant: refused at submission, alone and as two lines of one import.
        var grant = await SubmitGrant("SOD_DRAFTER", "CLAIMS_RELEASE_INSTRUCTION");
        grant.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await grant.Content.ReadAsStringAsync());
        (await grant.Code()).ShouldBe("ROLE_SEGREGATION_OF_DUTIES");
        await NewRole("SOD_IMPORTED");
        var imported = await ana.Send(HttpMethod.Post, $"{grants}/imports", new
        {
            fileRef = "sod.csv",
            lines = new object[]
            {
                new { code = "SOD_IMPORTED:PAYMENTS_BUILD_BATCH", roleCode = "SOD_IMPORTED", functionCode = "PAYMENTS_BUILD_BATCH", changeReason = "SRS-002" },
                new { code = "SOD_IMPORTED:PAYMENTS_RELEASE_BATCH", roleCode = "SOD_IMPORTED", functionCode = "PAYMENTS_RELEASE_BATCH", changeReason = "SRS-002" },
            },
        }, idempotent: true);
        imported.StatusCode.ShouldBe(HttpStatusCode.OK, await imported.Content.ReadAsStringAsync());
        var batch = (await imported.Json()).GetProperty("batchId").GetGuid();
        var submitted = await ana.Send(HttpMethod.Post, $"{grants}/imports/{batch}/submit");
        submitted.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await submitted.Content.ReadAsStringAsync());
        (await submitted.Code()).ShouldBe("ROLE_SEGREGATION_OF_DUTIES");

        // The user: a draft role, then a release role, refused.
        var user = await NewUser("sod-after");
        (await Assign(user, "SOD_DRAFTER")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var refused = await Assign(user, "SOD_RELEASER");
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict, await refused.Content.ReadAsStringAsync());
        (await refused.Code()).ShouldBe("SEGREGATION_OF_DUTIES");
    }

    /// <summary>
    /// SRS-002 (Ade, 20 November 2026, OQ-197): the platform baseline can't be retired or weakened. A tenant's row on a baseline pair, in either order, is refused
    /// as WARN or with an end date, and once in force can't be deprecated; a rule of the tenant's own on another pair, WARN included, is accepted.
    /// </summary>
    [Fact]
    public async Task ThePlatformBaseline_CannotBeRetiredOrWeakenedByATenant_SRS002()
    {
        var one = _factory.ClientFor(Operator, "platform-one");
        var two = _factory.ClientFor(Operator, "platform-two");
        var (tenantId, url) = await RegisterTenantAtAccessStep(one, "SODBASE");
        var request = new { tenantId, administrators = new[] { Person("sodbase-ana"), Person("sodbase-ben") }, functions = ConfiguringFunctions };
        var requestId = (await (await one.Send(HttpMethod.Post, "/api/v1/identity/tenant-administrators", request, idempotent: true)).Json()).GetProperty("id").GetGuid();
        await _factory.DrainOutbox();
        (await two.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{await Approval(one, requestId)}/approve", ifMatch: "\"1\"")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await _factory.DrainOutbox();
        var ana = _factory.ClientFor(tenantId, "sodbase-ana");
        var ben = _factory.ClientFor(tenantId, "sodbase-ben");
        foreach (var admin in new[] { ana, ben })
        {
            (await admin.GetAsync("/api/v1/identity/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await CompleteOnboardingAndActivate(one, url, (await one.GetAsync(url)).ETag(), "ORGANISATION_AND_ACCESS");
        const string rules = "/api/v1/identity/static/segregation-of-duties-conflict-rules";

        async Task<HttpResponseMessage> Submit(string code, object fields)
        {
            var body = System.Text.Json.JsonSerializer.SerializeToElement(fields);
            var draft = new Dictionary<string, object?> { ["code"] = code, ["changeReason"] = "SRS-002" };
            foreach (var field in body.EnumerateObject())
            {
                draft[field.Name] = field.Value;
            }

            var created = await ana.Send(HttpMethod.Post, rules, draft, idempotent: true);
            created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
            return await ana.Send(HttpMethod.Post, $"{rules}/{code}/versions/1/submit", ifMatch: created.ETag());
        }

        async Task Refused(HttpResponseMessage response)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await response.Content.ReadAsStringAsync());
            (await response.Code()).ShouldBe("SEGREGATION_BASELINE_PROTECTED");
        }

        // Weakened: WARN on a baseline pair, in its order or the other; an end date.
        await Refused(await Submit("SOD_WEAK", new { functionA = "CLAIMS_DRAFT_INSTRUCTION", functionB = "CLAIMS_RELEASE_INSTRUCTION", conflict = "WARN" }));
        await Refused(await Submit("SOD_WEAK_REV", new { functionA = "PAYMENTS_RELEASE_BATCH", functionB = "PAYMENTS_BUILD_BATCH", conflict = "WARN" }));
        await Refused(await Submit("SOD_ENDING", new
        {
            functionA = "PARTY_ADD_BANK_ACCOUNT", functionB = "PARTY_BANK_ACCOUNT_VERIFY", conflict = "BLOCK", effectiveTo = TestClock.Today.AddDays(30),
        }));

        // As BLOCK with no end date it is accepted, and once in force it can't be deprecated.
        (await Submit("SOD_CLAIM_PAYMENT", new { functionA = "CLAIMS_DRAFT_INSTRUCTION", functionB = "CLAIMS_RELEASE_INSTRUCTION", conflict = "BLOCK" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        await ApproveInTenant(ana, ben, tenantId, StaticDataApprovals.BusinessRefType, "SOD_CLAIM_PAYMENT@1");
        await Activate(ana, $"{rules}/SOD_CLAIM_PAYMENT");
        var published = (await (await ana.GetAsync($"{rules}/SOD_CLAIM_PAYMENT/versions")).Json())[0];
        await Refused(await ana.Send(HttpMethod.Post, $"{rules}/SOD_CLAIM_PAYMENT/versions/1/deprecate",
            ifMatch: $"\"{published.GetProperty("rowVersion").GetInt64()}\""));

        // The tenant's own rule on another pair, even WARN, is its own to set.
        (await Submit("SOD_OWN", new { functionA = "CLAIMS_SUBMIT_INSTRUCTION", functionB = "CLAIMS_MOVE_RESERVE", conflict = "WARN" })).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static readonly string[] ConfiguringFunctions =
    [
        "ASSIGN_ROLE", "IDENTITY_USERS", "PROVISION_USER", "SD_IDENTITY_AND_ACCESS_MANAGEMENT_APPROVE", "SD_IDENTITY_AND_ACCESS_MANAGEMENT_MAINTAIN",
        "SD_IDENTITY_AND_ACCESS_MANAGEMENT_VIEW", "STATIC_DATA_CHANGE_REQUESTS", "WORKFLOW_APPROVALS", "WORKFLOW_APPROVE", "WORKFLOW_REJECT",
    ];

    /// <summary>Publishes an approved first version, so that it is in force.</summary>
    private static async Task Activate(HttpClient client, string record)
    {
        var approved = (await (await client.GetAsync($"{record}/versions")).Json())[0];
        approved.GetProperty("recordStatus").GetString().ShouldBe("APPROVED");
        var activated = await client.Send(HttpMethod.Post, $"{record}/versions/1/activate", ifMatch: $"\"{approved.GetProperty("rowVersion").GetInt64()}\"");
        activated.StatusCode.ShouldBe(HttpStatusCode.OK, await activated.Content.ReadAsStringAsync());
    }

    /// <summary>The waiting approval for a record in the tenant: its maker cannot decide it, the other administrator approves it.</summary>
    private async Task ApproveInTenant(HttpClient maker, HttpClient checker, Guid tenantId, string businessRefType, string reference)
    {
        await _factory.DrainOutbox();
        var refId = businessRefType == StaticDataApprovals.BusinessRefType
            ? await fixture.Database.ScalarAsOwner<Guid>("SELECT id FROM sdm.change_request WHERE tenant_id = @t AND record_key = @k", ("t", tenantId), ("k", reference))
            : Guid.Parse(reference);
        var page = await (await checker.GetAsync($"/api/v1/workflow/approvals?status=Requested&businessRefType={businessRefType}&businessRefId={refId}")).Json();
        var approval = page.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid();
        (await (await maker.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"")).Code()).ShouldBe("MAKER_CHECKER_SAME_PERSON");
        var approved = await checker.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"");
        approved.StatusCode.ShouldBe(HttpStatusCode.NoContent, await approved.Content.ReadAsStringAsync());
        await _factory.DrainOutbox();
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

    /// <summary>
    /// SRS-002 (OQ-197): a new tenant's TENANT_ADMIN role may not hold both sides of a platform baseline rule, the check a role's function grant gets.
    /// Asked for, it is refused with 422 ROLE_SEGREGATION_OF_DUTIES on functions, naming the pair; a request already made with both (before this check)
    /// is not applied when approved, so the tenant never starts with administrators refused every release and verify.
    /// </summary>
    [Fact]
    public async Task FirstAdministrators_WhoseFunctionsHoldBothSidesOfABaselineRule_AreRefused_SRS002()
    {
        var one = _factory.ClientFor(Operator, "platform-one");
        var two = _factory.ClientFor(Operator, "platform-two");
        var (tenantId, _) = await RegisterTenantAtAccessStep(one, "SODADM");
        string[] both = ["CLAIM_PAYMENT_INSTRUCTIONS", "CLAIMS_DRAFT_INSTRUCTION", "CLAIMS_RELEASE_INSTRUCTION"];
        var refused = await one.Send(HttpMethod.Post, "/api/v1/identity/tenant-administrators",
            new { tenantId, administrators = new[] { Person("sodadm-a"), Person("sodadm-b") }, functions = AdminFunctions.Concat(both).ToArray() }, idempotent: true);
        var body = await refused.Content.ReadAsStringAsync();
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, body);
        var problem = System.Text.Json.JsonDocument.Parse(body).RootElement;
        problem.GetProperty("code").GetString().ShouldBe("ROLE_SEGREGATION_OF_DUTIES");
        var messages = problem.GetProperty("errors").GetProperty("functions").EnumerateArray().Select(m => m.GetString()!).ToList();
        messages.ShouldHaveSingleItem().ShouldContain("CLAIMS_DRAFT_INSTRUCTION");
        messages[0].ShouldContain("CLAIMS_RELEASE_INSTRUCTION");
        messages[0].ShouldContain("SOD_CLAIM_PAYMENT");

        // A request made before the check, with both sides: approving it creates nothing.
        var requestId = (await (await one.Send(HttpMethod.Post, "/api/v1/identity/tenant-administrators", Request(tenantId, "sodadm-admin"), idempotent: true))
            .Json()).GetProperty("id").GetGuid();
        await fixture.Database.ExecuteAsOwner(
            "UPDATE iam.tenant_administrator_request SET functions = functions || ARRAY['CLAIMS_DRAFT_INSTRUCTION', 'CLAIMS_RELEASE_INSTRUCTION'] WHERE id = @r",
            ("r", requestId));
        await _factory.DrainOutbox();
        (await two.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{await Approval(one, requestId)}/approve", ifMatch: "\"1\"")).StatusCode
            .ShouldBe(HttpStatusCode.NoContent);
        await _factory.DrainOutbox();

        (await (await one.GetAsync($"/api/v1/identity/tenant-administrators/{requestId}")).Json()).GetProperty("status").GetString()
            .ShouldBe("PendingApproval", "the grant isn't applied");
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM iam.user_account WHERE tenant_id = @t", ("t", tenantId))).ShouldBe(0);
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
        var access = readiness.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("step").GetString() == "ORGANISATION_AND_ACCESS");
        access.GetProperty("label").GetString().ShouldBe("Organisation and access"); // BR-020
        access.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("code").GetString() == "FIRST_ADMINISTRATOR")
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

    /// <summary>OQ-190 (Ade, 19 November 2026): the first administrators' functions follow Identity's one rule: ASSIST_PRODUCT_DRAFT_RUN only with its review.</summary>
    [Fact]
    public async Task ARequestNamingTheDraftRunWithoutItsReview_IsRefused_OQ190()
    {
        var one = _factory.ClientFor(Operator, "platform-one");
        var (tenantId, _) = await RegisterTenantAtAccessStep(one, "RUNREV");
        var body = new { tenantId, administrators = new[] { Person("run-rev"), Person("run-rev-2") }, functions = new[] { "IDENTITY_USERS", "ASSIST_PRODUCT_DRAFT_RUN" } };

        var refused = await one.Send(HttpMethod.Post, "/api/v1/identity/tenant-administrators", body, idempotent: true);

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var problem = await refused.Json();
        problem.GetProperty("code").GetString().ShouldBe("ASSIST_RUN_NEEDS_REVIEW");
        problem.GetProperty("errors").GetProperty("functions")[0].GetString()!.ShouldContain("ASSIST_PRODUCT_DRAFT_REVIEW");
    }

    /// <summary>
    /// BR-088: the request names only functions the new tenant can hold. A tenant entitled to GENERAL only, with one
    /// function switched off, is refused a Life function (FEATURE_NOT_ENTITLED) and the switched-off one
    /// (FUNCTION_DISABLED), each named; General and base functions are accepted, as the template read for it marks them.
    /// </summary>
    [Fact]
    public async Task ARequestNamingAFunctionTheTenantCannotHold_IsRefused_NamingEach_BR088()
    {
        var one = _factory.ClientFor(Operator, "platform-one");
        var (tenantId, _) = await RegisterTenantAtAccessStep(one, "GENONLY");
        await fixture.Database.ExecuteAsOwner("""
            UPDATE tenant.cfg_feature_entitlements SET record_status = 'RETIRED' WHERE tenant = 'GENONLY' AND feature <> 'GENERAL';
            INSERT INTO tenant.cfg_feature_entitlements (tenant_id, code, version, effective_from, record_status, created_by, change_reason, tenant, feature, enabled)
            VALUES (NULL, 'GENONLY:GENERAL', 1, DATE '2000-01-01', 'EFFECTIVE', 'seed', 'test seed', 'GENONLY', 'GENERAL', true)
            ON CONFLICT DO NOTHING
            """);
        var catalogue = Insys.BuildingBlocks.Api.Functions.FunctionRegistry.All().Select(f => f.Function)
            .Where(f => f.Audiences.Contains("STAFF") && f.Kind != Insys.BuildingBlocks.Api.Functions.FunctionKind.Job).ToList();
        var life = catalogue.First(f => f.Feature == "LIFE").Code;
        var general = catalogue.First(f => f.Feature == "GENERAL").Code;
        await fixture.Database.SeedStatic(tenantId, "tenant.cfg_tenant_function_availability", "EFFECTIVE", "GENONLY-OFF", new DateOnly(2000, 1, 1),
            ("function_code", "INTEGRATION_DEAD_LETTERS"), ("enabled", false));

        var refused = await one.Send(HttpMethod.Post, "/api/v1/identity/tenant-administrators", new
        {
            tenantId, administrators = new[] { Person("genonly-a"), Person("genonly-b") }, functions = new[] { "IDENTITY_USERS", general, life, "INTEGRATION_DEAD_LETTERS" },
        }, idempotent: true);

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await refused.Content.ReadAsStringAsync());
        (await refused.Json()).GetProperty("errors").GetProperty("functions").EnumerateArray().Select(e => e.GetString()).ShouldBe(new[]
        {
            "INTEGRATION_DEAD_LETTERS: FUNCTION_DISABLED, the tenant has switched it off.",
            $"{life}: FEATURE_NOT_ENTITLED, the tenant is not entitled to its feature.",
        }.OrderBy(m => m, StringComparer.Ordinal));

        var accepted = await one.Send(HttpMethod.Post, "/api/v1/identity/tenant-administrators", new
        {
            tenantId, administrators = new[] { Person("genonly-a"), Person("genonly-b") }, functions = new[] { "IDENTITY_USERS", general },
        }, idempotent: true);
        accepted.StatusCode.ShouldBe(HttpStatusCode.Created, await accepted.Content.ReadAsStringAsync());
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
