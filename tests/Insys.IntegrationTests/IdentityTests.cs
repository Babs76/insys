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
using Insys.Testing;

namespace Insys.IntegrationTests;

/// <summary>Identity: users, role assignments and per-request permissions (OQ-014, IAM-001 to IAM-005, SRS-002).</summary>
public sealed class IdentityTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private const string Admin = "iam-admin";
    private InsysApiFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = await fixture.Host();
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, Admin,
            IdentityPermissions.UserRead, IdentityPermissions.UserCreate, IdentityPermissions.UserUpdate);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>The ETag a change returns takes the next change (an endpoint that sets it from the returned version, with no body).</summary>
    [Fact]
    public async Task ARoleChangesReturnedETag_TakesTheNextChange_AndTheOneBeforeItIsRefused()
    {
        var userId = await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "clerk-chain", IntegrationPermissions.DeadLetterRead);
        var role = await fixture.Database.ScalarAsOwner<string>("SELECT role_code FROM iam.user_role_assignment WHERE user_id = @u", ("u", userId));
        var admin = _factory.ClientFor(DatabaseFixture.TenantA, Admin);
        var version = (await admin.GetAsync($"/api/v1/identity/users/{userId}")).ETag();
        await (await admin.Send(HttpMethod.Post, $"/api/v1/identity/users/{userId}/revoke-role", new { roleCode = role }, ifMatch: version))
            .ShouldChain(version, etag => admin.Send(HttpMethod.Post, $"/api/v1/identity/users/{userId}/assign-role", new { roleCode = role }, ifMatch: etag));
    }

    [Fact]
    public async Task RevokedRole_DeniesTheNextRequestWithTheSameToken_IAM005()
    {
        var clerkId = await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "clerk-revoke", IntegrationPermissions.DeadLetterRead);
        var role = await fixture.Database.ScalarAsOwner<string>("SELECT role_code FROM iam.user_role_assignment WHERE user_id = @u", ("u", clerkId));
        var clerk = _factory.ClientFor(DatabaseFixture.TenantA, "clerk-revoke");
        (await clerk.GetAsync("/api/v1/integration/dead-letters")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var admin = _factory.ClientFor(DatabaseFixture.TenantA, Admin);
        var user = await admin.GetAsync($"/api/v1/identity/users/{clerkId}");
        var revoked = await admin.Send(HttpMethod.Post, $"/api/v1/identity/users/{clerkId}/revoke-role", new { roleCode = role }, ifMatch: user.ETag());
        revoked.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await clerk.GetAsync("/api/v1/integration/dead-letters")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // GAP-020: the assignment is explicitly Revoked, with when and by whom.
        var assignment = (await (await admin.GetAsync($"/api/v1/identity/users/{clerkId}")).Json()).GetProperty("roleAssignments").EnumerateArray().Single();
        assignment.GetProperty("status").GetString().ShouldBe("Revoked");
        assignment.GetProperty("revokedAt").GetDateTimeOffset().ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-5));
        var adminId = await fixture.Database.ScalarAsOwner<Guid>("SELECT id FROM iam.user_account WHERE tenant_id = @t AND username = @n",
            ("t", DatabaseFixture.TenantA), ("n", Admin));
        assignment.GetProperty("revokedBy").GetGuid().ShouldBe(adminId);
        (await fixture.Database.ScalarAsOwner<string>("SELECT status FROM iam.user_role_assignment WHERE user_id = @u", ("u", clerkId))).ShouldBe("REVOKED");
    }

    [Fact]
    public async Task AssignedRole_GrantsPermissionsOnTheNextRequest_SRS001()
    {
        var userId = await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "clerk-assign");
        var role = await fixture.Database.RoleWithPermissions(DatabaseFixture.TenantA, IntegrationPermissions.DeadLetterRead);
        var clerk = _factory.ClientFor(DatabaseFixture.TenantA, "clerk-assign");
        (await clerk.GetAsync("/api/v1/integration/dead-letters")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var admin = _factory.ClientFor(DatabaseFixture.TenantA, Admin);
        var user = await admin.GetAsync($"/api/v1/identity/users/{userId}");
        (await admin.Send(HttpMethod.Post, $"/api/v1/identity/users/{userId}/assign-role", new { roleCode = role }, ifMatch: user.ETag()))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await clerk.GetAsync("/api/v1/integration/dead-letters")).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM iam.outbox_event WHERE event_type = 'iam.RoleAssigned' AND payload->>'userId' = @u",
            ("u", userId.ToString()))).ShouldBe(1);
    }

    [Fact]
    public async Task DisabledUser_IsDeniedOnTheNextRequest_IAM005()
    {
        var userId = await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "clerk-disable", IntegrationPermissions.DeadLetterRead);
        var clerk = _factory.ClientFor(DatabaseFixture.TenantA, "clerk-disable");
        (await clerk.GetAsync("/api/v1/integration/dead-letters")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var admin = _factory.ClientFor(DatabaseFixture.TenantA, Admin);
        var user = await admin.GetAsync($"/api/v1/identity/users/{userId}");
        (await admin.Send(HttpMethod.Post, $"/api/v1/identity/users/{userId}/disable", ifMatch: user.ETag())).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await clerk.GetAsync("/api/v1/integration/dead-letters")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ProvisionedUser_IsInvited_UntilTheFirstSignIn()
    {
        var admin = _factory.ClientFor(DatabaseFixture.TenantA, Admin);
        var created = await admin.Send(HttpMethod.Post, "/api/v1/identity/users",
            new { username = "new.user", displayName = "New User", idpCode = "TEST", externalSubject = "new-user-sub", mfaEnrolled = false }, idempotent: true);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var id = (await created.Json()).GetProperty("id").GetGuid();
        (await (await admin.GetAsync($"/api/v1/identity/users/{id}")).Json()).GetProperty("status").GetString().ShouldBe("Invited");

        await _factory.ClientFor(DatabaseFixture.TenantA, "new-user-sub").GetAsync("/api/v1/integration/dead-letters");

        (await fixture.Database.ScalarAsOwner<string>("SELECT status FROM iam.user_account WHERE id = @id", ("id", id))).ShouldBe("ACTIVE");
    }

    [Fact]
    public async Task AssignRole_ThatCombinesBlockedFunctions_IsRefused_SRS002()
    {
        // ADR-026 §4: the rule pairs functions, checked across the user's roles in effect.
        await fixture.Database.ExecuteAsOwner("""
            INSERT INTO iam.cfg_segregation_of_duties_conflict_rules
              (tenant_id, code, version, effective_from, record_status, created_by, change_reason, function_a, function_b, conflict)
            VALUES (@t, 'SOD_BANK', 1, current_date - 1, 'EFFECTIVE', 'seed', 'test', 'PARTY_BANK_ACCOUNTS', 'PARTY_BANK_ACCOUNT_VERIFY', 'BLOCK')
            """, ("t", DatabaseFixture.TenantA));
        var userId = await fixture.Database.UserWithFunctions(DatabaseFixture.TenantA, "clerk-sod", "PARTY_BANK_ACCOUNTS");
        var approver = await fixture.Database.RoleWithFunctions(DatabaseFixture.TenantA, "PARTY_BANK_ACCOUNT_VERIFY");

        var admin = _factory.ClientFor(DatabaseFixture.TenantA, Admin);
        var user = await admin.GetAsync($"/api/v1/identity/users/{userId}");
        var response = await admin.Send(HttpMethod.Post, $"/api/v1/identity/users/{userId}/assign-role", new { roleCode = approver }, ifMatch: user.ETag());

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Code()).ShouldBe("SEGREGATION_OF_DUTIES");
    }

    [Fact]
    public async Task AssignRole_ThatRequiresMfa_ToAUserWithoutMfa_IsRefused_IAM003()
    {
        var privileged = await fixture.Database.RoleWithFunctions(DatabaseFixture.TenantA, "PARTIES");
        await fixture.Database.ExecuteAsOwner("""
            INSERT INTO iam.cfg_authentication_and_session_policies
              (tenant_id, code, version, effective_from, record_status, created_by, change_reason, policy_code, mfa_required_roles, session_timeout, lockout_attempts)
            VALUES (@t, 'MFA_POLICY', 1, current_date - 1, 'EFFECTIVE', 'seed', 'test', 'MFA_POLICY', @roles, interval '30 minutes', 5)
            """, ("t", DatabaseFixture.TenantA), ("roles", new[] { privileged }));
        var admin = _factory.ClientFor(DatabaseFixture.TenantA, Admin);
        var created = await admin.Send(HttpMethod.Post, "/api/v1/identity/users",
            new { username = "no.mfa", displayName = "No MFA", idpCode = "TEST", externalSubject = "no-mfa", mfaEnrolled = false }, idempotent: true);
        var id = (await created.Json()).GetProperty("id").GetGuid();

        var response = await admin.Send(HttpMethod.Post, $"/api/v1/identity/users/{id}/assign-role", new { roleCode = privileged }, ifMatch: created.ETag());

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Code()).ShouldBe("MFA_REQUIRED");
    }

    [Fact]
    public async Task Users_AreTenantScoped_IAM001()
    {
        var other = await fixture.Database.UserWithPermissions(DatabaseFixture.TenantB, "b-user");

        (await _factory.ClientFor(DatabaseFixture.TenantA, Admin).GetAsync($"/api/v1/identity/users/{other}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // The same subject in another tenant has none of the first tenant's permissions.
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantB, Admin);
        (await _factory.ClientFor(DatabaseFixture.TenantB, Admin).GetAsync("/api/v1/identity/users")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
