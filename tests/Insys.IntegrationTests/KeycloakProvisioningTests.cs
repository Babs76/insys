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
