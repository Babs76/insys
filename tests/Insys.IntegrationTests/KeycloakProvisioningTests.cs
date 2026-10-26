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
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.Identity.Contracts;
using Insys.Integration.Infrastructure.Parking;
using Insys.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Testcontainers.Keycloak;

namespace Insys.IntegrationTests;

/// <summary>
/// INSYS creates the Keycloak users of its own realm (docs/specs/addenda/keycloak-user-provisioning.md): exactly one per
/// INSYS user, with the user's tenant; a retry links the one Keycloak has; a username of another tenant's user, or
/// Keycloak down, parks the delivery (ADR-022); disabling the user disables it in Keycloak; and the service client's
/// secret is never logged. Against the committed realm export in a Keycloak container.
/// </summary>
public sealed class KeycloakProvisioningTests(DatabaseFixture fixture, KeycloakProvisioningTests.Realm realm)
    : IClassFixture<DatabaseFixture>, IClassFixture<KeycloakProvisioningTests.Realm>, IAsyncLifetime
{
    private static readonly Guid Tenant = DatabaseFixture.TenantA;
    private readonly CapturedLogs _logs = new();
    private InsysApiFactory _factory = null!;
    private HttpClient _admin = null!;

    /// <summary>One Keycloak per class, from the committed realm export; the service client's generated secret is read from it.</summary>
    public sealed class Realm : IAsyncLifetime
    {
        private readonly KeycloakContainer _keycloak = new KeycloakBuilder("quay.io/keycloak/keycloak:26.7.5")
            .WithRealm(Path.Combine(RepositoryPaths.Root, "deploy", "keycloak", "insys-realm.json"))
            .WithEnvironment("INSYS_DEV_ADMIN_PASSWORD", $"dev-{Guid.NewGuid():N}")
            .Build();

        public HttpClient Http { get; } = new();

        public string Base => _keycloak.GetBaseAddress().TrimEnd('/');

        public string Secret { get; private set; } = "";

        public async ValueTask InitializeAsync()
        {
            await _keycloak.StartAsync();
            var clients = await Admin(HttpMethod.Get, "/clients?clientId=insys-user-admin");
            var id = clients[0].GetProperty("id").GetString();
            Secret = (await Admin(HttpMethod.Get, $"/clients/{id}/client-secret")).GetProperty("value").GetString()!;
        }

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await _keycloak.DisposeAsync();
        }

        /// <summary>The master realm's admin, for the test's own look at realm insys.</summary>
        public async Task<JsonElement> Admin(HttpMethod method, string path, object? body = null)
        {
            using var token = await Http.PostAsync($"{Base}/realms/master/protocol/openid-connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password", ["client_id"] = "admin-cli", ["username"] = "admin", ["password"] = "admin",
            }));
            var access = (await token.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString();
            using var request = new HttpRequestMessage(method, $"{Base}/admin/realms/insys{path}") { Content = body is null ? null : JsonContent.Create(body) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            using var response = await Http.SendAsync(request);
            response.IsSuccessStatusCode.ShouldBeTrue($"{method} {path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
            var text = await response.Content.ReadAsStringAsync();
            return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
        }

        public async Task<JsonElement[]> Users(string username) => (await Admin(HttpMethod.Get, $"/users?username={username}&exact=true&briefRepresentation=false"))
            .EnumerateArray().ToArray();
    }

    public async ValueTask InitializeAsync()
    {
        _factory = new InsysApiFactory(fixture.Database, Settings(realm.Base), logs: _logs);
        await fixture.Database.UserWithPermissions(Tenant, "kc-admin", IdentityPermissions.UserCreate, IdentityPermissions.UserUpdate, IdentityPermissions.UserRead);
        _admin = _factory.ClientFor(Tenant, "kc-admin");
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Provisioning_CreatesExactlyOneKeycloakUser_InTheUsersTenant_AndDisablingDisablesIt_KeycloakProvisioning()
    {
        var user = await Provision(_admin, "jane.doe", mfa: true);
        (await Status(user)).ShouldBe("PENDING_IDENTITY");
        await _factory.DrainOutbox();

        var created = (await realm.Users("tena.jane.doe")).Single();
        created.GetProperty("attributes").GetProperty("tenant_id")[0].GetString().ShouldBe(Tenant.ToString());
        (await Status(user)).ShouldBe("INVITED");
        (await Subject(user)).ShouldBe(created.GetProperty("id").GetString());

        // No SMTP in this realm: the user is linked and active, and only the email is parked to be retried.
        (await Parked("iam.KeycloakProvisioning.UserInvitationRequested", user)).ShouldBe("KEYCLOAK_EMAIL_UNSENT insys");

        // A replayed request creates nothing more.
        await Deliver(new UserIdentityRequested(user), "iam.UserIdentityRequested", user);
        (await realm.Users("tena.jane.doe")).Length.ShouldBe(1);

        // The user signs in with no roles: it is recognised, and can do nothing until a role is assigned.
        var signedIn = _factory.ClientFor(Tenant, (await Subject(user))!);
        var me = await signedIn.GetAsync("/api/v1/identity/me");
        me.StatusCode.ShouldBe(HttpStatusCode.OK, await me.Content.ReadAsStringAsync());
        (await me.Json()).GetProperty("permissions").GetArrayLength().ShouldBe(0);
        (await signedIn.GetAsync("/api/v1/identity/users")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await Status(user)).ShouldBe("ACTIVE");

        // Disabling the user disables it in Keycloak.
        var etag = (await _admin.GetAsync($"/api/v1/identity/users/{user}")).ETag();
        (await _admin.Send(HttpMethod.Post, $"/api/v1/identity/users/{user}/disable", ifMatch: etag)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await _factory.DrainOutbox();
        (await realm.Users("tena.jane.doe")).Single().GetProperty("enabled").GetBoolean().ShouldBeFalse();

        // The service client's secret is never logged.
        _logs.Messages.ShouldNotContain(m => m.Contains(realm.Secret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARetryAfterKeycloakCreatedTheUser_LinksIt_AndAnotherTenantsUsername_Parks_KeycloakProvisioning()
    {
        // As if Keycloak had created the user before a crash, and INSYS never recorded it.
        await realm.Admin(HttpMethod.Post, "/users", new
        {
            username = "tena.sam.lee", email = "sam@example.com", enabled = true, firstName = "Sam", lastName = "Lee",
            attributes = new Dictionary<string, string[]> { ["tenant_id"] = [Tenant.ToString()] },
        });
        var existing = (await realm.Users("tena.sam.lee")).Single().GetProperty("id").GetString();
        var sam = await Provision(_admin, "sam.lee");
        await _factory.DrainOutbox();
        (await Subject(sam)).ShouldBe(existing);
        (await realm.Users("tena.sam.lee")).Length.ShouldBe(1);

        // The same Keycloak username held by another tenant's user: parked for the operator, not linked.
        await realm.Admin(HttpMethod.Post, "/users", new
        {
            username = "tena.kim.wu", email = "kim@example.com", enabled = true, firstName = "Kim", lastName = "Wu",
            attributes = new Dictionary<string, string[]> { ["tenant_id"] = [DatabaseFixture.TenantB.ToString()] },
        });
        var kim = await Provision(_admin, "kim.wu");
        await _factory.DrainOutbox();
        (await Parked("iam.KeycloakProvisioning.UserIdentityRequested", kim)).ShouldBe("KEYCLOAK_USER_CONFLICT username");
        (await Status(kim)).ShouldBe("PENDING_IDENTITY");
    }

    [Fact]
    public async Task KeycloakDown_ParksTheCreation_AndItIsCreatedOnceKeycloakAnswers_KeycloakProvisioning()
    {
        await using var down = new InsysApiFactory(fixture.Database, Settings("http://127.0.0.1:1"));
        var user = await Provision(down.ClientFor(Tenant, "kc-admin"), "lee.ray");
        await down.DrainOutbox();
        (await Parked("iam.KeycloakProvisioning.UserIdentityRequested", user)).ShouldBe("KEYCLOAK_UNAVAILABLE insys");
        (await Status(user)).ShouldBe("PENDING_IDENTITY");

        // Keycloak answers again (here, a host that reaches it): an operator asks for the retry (as POST /integration/parked-deliveries/retry
        // does), and the parking job retries the delivery.
        await fixture.Database.ExecuteAsOwner("UPDATE integration.parked_delivery SET retry_requested_at = now() WHERE prerequisite_code = 'KEYCLOAK_UNAVAILABLE'");
        await _factory.Services.GetRequiredService<ParkingJob>().RunForTenant(Tenant, CancellationToken.None);
        await _factory.DrainOutbox();
        (await Status(user)).ShouldBe("INVITED");
        (await realm.Users("tena.lee.ray")).Length.ShouldBe(1);
    }

    private IReadOnlyDictionary<string, string?> Settings(string baseUrl) => new Dictionary<string, string?>
    {
        ["Identity:Keycloak:BaseUrl"] = baseUrl, ["Identity:Keycloak:ClientSecret"] = realm.Secret, ["Identity:Keycloak:Timeout"] = "00:00:05",
        ["Identity:PermissionCache:AllowMultipleInstances"] = "true",
    };

    /// <summary>
    /// BR-006: a name Keycloak's person-name validator refuses is a field error when the user is provisioned; and a refusal
    /// from Keycloak that retrying cannot fix parks under its own code with Keycloak's reason, never as unavailable.
    /// </summary>
    [Fact]
    public async Task ANameKeycloakRefuses_IsAFieldError_AndARefusalParksWithItsReason_BR006()
    {
        var refused = await _admin.Send(HttpMethod.Post, "/api/v1/identity/users", new
        {
            username = "pat.ng", displayName = "Pat Ng (Contractor)", idpCode = "KEYCLOAK", email = "pat.ng@example.com", mfaEnrolled = false,
        }, idempotent: true);
        var body = await refused.Content.ReadAsStringAsync();
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, body);
        (await refused.Json()).GetProperty("errors").GetProperty("displayName").EnumerateArray().Select(e => e.GetString())
            .ShouldContain("Keycloak does not accept these characters in a name: ( ).", body);

        // A name that reaches Keycloak anyway (changed in the database after provisioning, say): Keycloak's 400 parks the
        // creation under KEYCLOAK_USER_REFUSED with Keycloak's reason, for someone to correct, not as Keycloak being down.
        var user = await Provision(_admin, "lou.ng");
        await fixture.Database.ExecuteAsOwner("UPDATE iam.user_account SET display_name = 'Lou (Temp) Ng' WHERE id = @u", ("u", user));
        await _factory.DrainOutbox();

        var parked = await Parked("iam.KeycloakProvisioning.UserIdentityRequested", user);
        parked.ShouldNotBeNull();
        parked.ShouldStartWith("KEYCLOAK_USER_REFUSED ");
        parked.ShouldNotContain("KEYCLOAK_UNAVAILABLE");
        (await realm.Users("tena.lou.ng")).ShouldBeEmpty();
    }

    /// <summary>
    /// BR-018: an approved first administrator is not usable until its Keycloak identity is linked. Before then a call
    /// with its Keycloak token is refused with IDENTITY_NOT_LINKED (retryable), not FORBIDDEN, and the request reads its own
    /// state, not Provisioned. Once linked, the next call works at once, with no wait for a cached refusal to expire.
    /// </summary>
    [Fact]
    public async Task AnApprovedAdministrator_IsRefusedWithIdentityNotLinked_UntilLinked_ThenWorksAtOnce_BR018()
    {
        var (one, two) = await PlatformAdministrators();
        var target = await TenantAtAccessStep(one, "KCADM");
        await using var down = new InsysApiFactory(fixture.Database, Settings("http://127.0.0.1:1"));
        var requestId = await ApprovedAdministrators(down, target, "kcadm-a", "kcadm-b");

        // Keycloak unreachable: the users exist, waiting for their identities, and the request says so.
        (await RequestState(requestId)).ShouldBe("IdentityParked");
        var waiting = await _factory.ClientFor(target, "kc-not-yet-linked", username: "kcadm.kcadm-a").GetAsync("/api/v1/identity/me");
        waiting.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await waiting.Code()).ShouldBe("IDENTITY_NOT_LINKED");
        waiting.Headers.RetryAfter.ShouldNotBeNull();
        // A token for no user at all is still plainly refused.
        (await (await _factory.ClientFor(target, "nobody", username: "kcadm.nobody").GetAsync("/api/v1/identity/me")).Code()).ShouldBe("FORBIDDEN");

        // The operator retries once Keycloak is back; the parking job, in a host that reaches Keycloak, creates and links them.
        var retried = await _factory.ClientFor(DatabaseFixture.Operator, "kc-platform-one").Send(HttpMethod.Post,
            $"/api/v1/identity/tenant-administrators/{requestId}/retry-identity");
        retried.StatusCode.ShouldBe(HttpStatusCode.OK, await retried.Content.ReadAsStringAsync());
        (await RequestState(requestId)).ShouldBe("AwaitingIdentity");
        await _factory.Services.GetRequiredService<ParkingJob>().RunForTenant(target, CancellationToken.None);
        await _factory.DrainOutbox();
        (await RequestState(requestId)).ShouldBe("Provisioned");

        var subject = await fixture.Database.ScalarAsOwner<string>("SELECT external_subject FROM iam.user_account WHERE tenant_id = @t AND username = 'kcadm-a'",
            ("t", target));
        (await _factory.ClientFor(target, subject!, username: "kcadm.kcadm-a").GetAsync("/api/v1/identity/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// BR-018: an identity Keycloak refuses for good (another tenant's user holds the username) leaves the request
    /// IdentityFailed, with the reason; once the cause is fixed, the operator's retry returns it to AwaitingIdentity, and it
    /// is Provisioned when linked.
    /// </summary>
    [Fact]
    public async Task AnIdentityThatFails_ShowsIdentityFailed_WithTheReason_AndARetryAfterTheFixProvisionsIt_BR018()
    {
        var (one, _) = await PlatformAdministrators();
        var target = await TenantAtAccessStep(one, "KCFAIL");
        await realm.Admin(HttpMethod.Post, "/users", new
        {
            username = "kcfail.kcfail-a", email = "other@example.com", enabled = true, firstName = "Other", lastName = "Tenant",
            attributes = new Dictionary<string, string[]> { ["tenant_id"] = [DatabaseFixture.TenantB.ToString()] },
        });
        var requestId = await ApprovedAdministrators(_factory, target, "kcfail-a", "kcfail-b");

        var request = await (await _factory.ClientFor(DatabaseFixture.Operator, "kc-platform-one").GetAsync($"/api/v1/identity/tenant-administrators/{requestId}")).Json();
        request.GetProperty("status").GetString().ShouldBe("IdentityFailed");
        request.GetProperty("administrators").EnumerateArray().Select(a => (a.GetProperty("username").GetString(), a.GetProperty("identityState").GetString(),
            a.GetProperty("identityReason").ValueKind == JsonValueKind.Null ? null : a.GetProperty("identityReason").GetString()))
            .ShouldBe([("kcfail-a", "Failed", "KEYCLOAK_USER_CONFLICT username"), ("kcfail-b", "Linked", null)]);

        // The fix: the other tenant's Keycloak user is renamed away. Then the operator retries.
        var conflict = (await realm.Users("kcfail.kcfail-a")).Single().GetProperty("id").GetString();
        await realm.Admin(HttpMethod.Delete, $"/users/{conflict}");
        (await _factory.ClientFor(DatabaseFixture.Operator, "kc-platform-one").Send(HttpMethod.Post,
            $"/api/v1/identity/tenant-administrators/{requestId}/retry-identity")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RequestState(requestId)).ShouldBe("AwaitingIdentity");
        await _factory.Services.GetRequiredService<ParkingJob>().RunForTenant(target, CancellationToken.None);
        await _factory.DrainOutbox();
        (await RequestState(requestId)).ShouldBe("Provisioned");
    }

    private async Task<string> RequestState(Guid requestId) =>
        (await (await _factory.ClientFor(DatabaseFixture.Operator, "kc-platform-one").GetAsync($"/api/v1/identity/tenant-administrators/{requestId}")).Json())
            .GetProperty("status").GetString()!;

    /// <summary>Two platform administrators; the second holds authority for tenant administrator approvals (WFL-004).</summary>
    private async Task<(HttpClient One, HttpClient Two)> PlatformAdministrators()
    {
        string[] permissions =
        [
            Tenancy.Contracts.TenancyPermissions.Read, Tenancy.Contracts.TenancyPermissions.Create, Tenancy.Contracts.TenancyPermissions.Update,
            IdentityPermissions.UserRead, IdentityPermissions.UserCreate, Workflow.Contracts.WorkflowPermissions.ApprovalRead,
            Workflow.Contracts.WorkflowPermissions.ApprovalApprove,
        ];
        var db = fixture.Database;
        await db.UserWithPermissions(DatabaseFixture.Operator, "kc-platform-one", permissions);
        var second = await db.UserWithPermissions(DatabaseFixture.Operator, "kc-platform-two", permissions);
        var role = await db.RoleWithPermissions(DatabaseFixture.Operator);
        await db.AssignRole(DatabaseFixture.Operator, second, role);
        await db.ExecuteAsOwner($"""
            INSERT INTO workflow.cfg_delegation_and_approval_authority
              (tenant_id, code, version, effective_from, record_status, created_by, change_reason, role_code, transaction_type, limit_value, delegation_allowed)
            VALUES (@t, @code, 1, current_date - 1, 'EFFECTIVE', 'seed', 'test', @role, '{IdentityApprovals.TenantAdministratorApprovalType}', 0, false)
            ON CONFLICT DO NOTHING
            """, ("t", DatabaseFixture.Operator), ("code", "AUTH-" + role[..12]), ("role", role));
        return (_factory.ClientFor(DatabaseFixture.Operator, "kc-platform-one"), _factory.ClientFor(DatabaseFixture.Operator, "kc-platform-two"));
    }

    private async Task<Guid> TenantAtAccessStep(HttpClient operatorClient, string code)
    {
        var registered = await operatorClient.Send(HttpMethod.Post, "/api/v1/tenancy/tenants",
            new { tenantCode = code, legalName = code + " Ltd", isolationProfile = "B", timeZone = "Africa/Johannesburg" }, idempotent: true);
        registered.StatusCode.ShouldBe(HttpStatusCode.Created, await registered.Content.ReadAsStringAsync());
        var tenantId = (await registered.Json()).GetProperty("id").GetGuid();
        var url = $"/api/v1/tenancy/tenants/{tenantId}";
        var etag = (await operatorClient.GetAsync(url)).ETag();
        await fixture.Database.SeedReadyTenant(tenantId, TestClock.Today.AddDays(-30));
        foreach (var step in new[] { "PLATFORM_REFERENCE_DATA", "TENANT_REGISTRATION" })
        {
            var completed = await operatorClient.Send(HttpMethod.Post, $"{url}/complete-onboarding-step", new { step }, ifMatch: etag);
            completed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
            etag = completed.ETag();
        }

        return tenantId;
    }

    /// <summary>Two administrators of INSYS's own realm, requested and approved by two platform administrators through <paramref name="host"/>.</summary>
    private async Task<Guid> ApprovedAdministrators(InsysApiFactory host, Guid target, string first, string second)
    {
        var one = host.ClientFor(DatabaseFixture.Operator, "kc-platform-one");
        var body = new
        {
            tenantId = target,
            administrators = new[] { first, second }.Select(u => new { username = u, displayName = "Admin " + u, idpCode = "KEYCLOAK", email = $"{u}@example.com", mfaEnrolled = false }),
            functions = new[] { "IDENTITY_USERS" },
        };
        var requested = await one.Send(HttpMethod.Post, "/api/v1/identity/tenant-administrators", body, idempotent: true);
        requested.StatusCode.ShouldBe(HttpStatusCode.Created, await requested.Content.ReadAsStringAsync());
        var requestId = (await requested.Json()).GetProperty("id").GetGuid();
        await host.DrainOutbox();
        var page = await (await one.GetAsync(
            $"/api/v1/workflow/approvals?status=Requested&businessRefType={IdentityApprovals.TenantAdministratorBusinessRefType}&businessRefId={requestId}")).Json();
        var approval = page.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid();
        (await host.ClientFor(DatabaseFixture.Operator, "kc-platform-two").Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\""))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await host.DrainOutbox();
        return requestId;
    }

    private static async Task<Guid> Provision(HttpClient client, string username, bool mfa = false)
    {
        var created = await client.Send(HttpMethod.Post, "/api/v1/identity/users", new
        {
            username, displayName = "Test " + username, idpCode = "KEYCLOAK", email = $"{username}@example.com", mfaEnrolled = mfa,
        }, idempotent: true);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        return (await created.Json()).GetProperty("id").GetGuid();
    }

    private Task<string?> Status(Guid user) => fixture.Database.ScalarAsOwner<string>("SELECT status FROM iam.user_account WHERE id = @u", ("u", user));

    private Task<string?> Subject(Guid user) => fixture.Database.ScalarAsOwner<string>("SELECT external_subject FROM iam.user_account WHERE id = @u", ("u", user));

    private Task<string?> Parked(string consumer, Guid user) =>
        fixture.Database.ScalarAsOwner<string>("""
            SELECT prerequisite_code || ' ' || prerequisite_item FROM integration.parked_delivery
            WHERE consumer = @c AND status = 'PARKED' AND payload ->> 'userId' = @u
            """, ("c", consumer), ("u", user.ToString()));

    private async Task Deliver<T>(T integrationEvent, string type, Guid user) where T : IIntegrationEvent =>
        await _factory.Services.GetRequiredService<IEventBus>().Deliver(new EventEnvelope(Guid.CreateVersion7(), Tenant, type, 1, DateTimeOffset.UtcNow, "UserAccount", user,
            Guid.CreateVersion7(), null, JsonSerializer.SerializeToElement(integrationEvent, JsonSerializerOptions.Web)), CancellationToken.None);

    private sealed class CapturedLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose()
        {
        }

        private sealed class Logger(CapturedLogs owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Messages.Enqueue(formatter(state, exception) + " " + exception);
        }
    }
}
