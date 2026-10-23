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
    public async Task AssignRole_ThatCombinesBlockedPermissions_IsRefused_SRS002()
    {
        await fixture.Database.ExecuteAsOwner("""
            INSERT INTO iam.cfg_segregation_of_duties_conflict_rules
              (tenant_id, code, version, effective_from, record_status, created_by, change_reason, permission_a, permission_b, conflict)
            VALUES (@t, 'SOD_PAY', 1, current_date - 1, 'EFFECTIVE', 'seed', 'test', 'payment.create', 'payment.approve', 'BLOCK')
            """, ("t", DatabaseFixture.TenantA));
        var userId = await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "clerk-sod", "payment.create");
        var approver = await fixture.Database.RoleWithPermissions(DatabaseFixture.TenantA, "payment.approve");

        var admin = _factory.ClientFor(DatabaseFixture.TenantA, Admin);
        var user = await admin.GetAsync($"/api/v1/identity/users/{userId}");
        var response = await admin.Send(HttpMethod.Post, $"/api/v1/identity/users/{userId}/assign-role", new { roleCode = approver }, ifMatch: user.ETag());

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Code()).ShouldBe("SEGREGATION_OF_DUTIES");
    }

    [Fact]
    public async Task AssignRole_ThatRequiresMfa_ToAUserWithoutMfa_IsRefused_IAM003()
    {
        var privileged = await fixture.Database.RoleWithPermissions(DatabaseFixture.TenantA, "tenant_settings.update");
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
