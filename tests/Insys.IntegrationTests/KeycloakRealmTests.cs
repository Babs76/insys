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
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Insys.Testing;
using Testcontainers.Keycloak;

namespace Insys.IntegrationTests;

/// <summary>
/// The local identity provider (ADR-017, Phase 8 Workstream D) from the committed realm export: tokens carry sub and
/// tenant_id and the API's audience, and nothing else about the user; the API accepts them; and tenant_id can be changed
/// by an administrator only, never by the user through the account console.
/// </summary>
public sealed class KeycloakRealmTests(DatabaseFixture fixture, KeycloakRealmTests.Realm realm) : IClassFixture<DatabaseFixture>, IClassFixture<KeycloakRealmTests.Realm>
{
    private const string AdminSubject = "0199f000-0000-7000-8000-0000000000a1";
    private const string OperatorTenant = "0199f000-0000-7000-8000-000000000001";

    private HttpClient _http => realm.Http;

    private string Base => realm.Base;

    private static string DevPassword => Realm.DevPassword;

    /// <summary>One Keycloak per class, from the committed realm export, with two test-only clients.</summary>
    public sealed class Realm : IAsyncLifetime
    {
        public static readonly string DevPassword = $"dev-{Guid.NewGuid():N}";

        private readonly KeycloakContainer _keycloak = new KeycloakBuilder("quay.io/keycloak/keycloak:26.7.5")
            .WithRealm(Path.Combine(RepositoryPaths.Root, "deploy", "keycloak", "insys-realm.json"))
            .WithEnvironment("INSYS_DEV_ADMIN_PASSWORD", DevPassword)
            .Build();

        public HttpClient Http { get; } = new();

        public string Base => _keycloak.GetBaseAddress().TrimEnd('/');

        public async ValueTask InitializeAsync()
        {
            await _keycloak.StartAsync();

            // Test-only clients with the password grant, so the test signs in without a browser (the realm's insys-web
            // allows only Authorization Code with PKCE). insys-account-test carries the account audience and roles, as the
            // account console does, to try a self-service change.
            var admin = await Token(Http, Base, "master", "admin-cli", "admin", "admin");
            var accountMappers = new object[]
            {
                new { name = "account audience", protocol = "openid-connect", protocolMapper = "oidc-audience-mapper",
                    config = new Dictionary<string, string> { ["included.client.audience"] = "account", ["access.token.claim"] = "true" } },
                new { name = "client roles", protocol = "openid-connect", protocolMapper = "oidc-usermodel-client-role-mapper",
                    config = new Dictionary<string, string> { ["claim.name"] = "resource_access.${client_id}.roles", ["multivalued"] = "true", ["access.token.claim"] = "true", ["jsonType.label"] = "String" } },
            };
            foreach (var (clientId, fullScope, scopes, mappers) in new[]
                     {
                         ("insys-test", false, new[] { "basic", "insys-api" }, Array.Empty<object>()),
                         ("insys-account-test", true, new[] { "basic" }, accountMappers),
                     })
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{Base}/admin/realms/insys/clients")
                {
                    Content = JsonContent.Create(new
                    {
                        clientId, publicClient = true, directAccessGrantsEnabled = true, standardFlowEnabled = false, fullScopeAllowed = fullScope,
                        defaultClientScopes = scopes, protocolMappers = mappers,
                    }),
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
                (await Http.SendAsync(request)).StatusCode.ShouldBe(HttpStatusCode.Created);
            }
        }

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await _keycloak.DisposeAsync();
        }
    }

    [Fact]
    public async Task AccessToken_CarriesSubAndTenantId_AndTheApiAudience_AndNothingAboutTheUser()
    {
        var claims = Payload(await Token("insys", "insys-test", "insys-admin", DevPassword));

        claims.GetProperty("sub").GetString().ShouldBe(AdminSubject);
        claims.GetProperty("tenant_id").GetString().ShouldBe(OperatorTenant);
        Audiences(claims).ShouldContain("insys-core-api");
        foreach (var personal in new[] { "email", "name", "given_name", "family_name", "preferred_username", "realm_access", "resource_access" })
        {
            claims.TryGetProperty(personal, out _).ShouldBeFalse(personal);
        }
    }

    [Fact]
    public async Task Api_AcceptsTheRealmsToken()
    {
        await fixture.Database.UserWithPermissions(DatabaseFixture.Operator, AdminSubject, "static_data.read");
        var factory = await fixture.Host(new Dictionary<string, string?>
        {
            ["Authentication:Authority"] = $"{Base}/realms/insys", ["Authentication:Issuer"] = null, ["Authentication:Audience"] = "insys-core-api",
            ["Authentication:RequireHttpsMetadata"] = "false", ["Authentication:DevelopmentSigningKey"] = "",
        });
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Token("insys", "insys-test", "insys-admin", DevPassword));

        var response = await client.GetAsync("/api/v1/static-data/static/currencies");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TenantId_IsEditableByAdministratorsOnly_NeverThroughTheAccountConsole()
    {
        var admin = await Token("master", "admin-cli", "admin", "admin");
        var profile = await Get<JsonElement>($"{Base}/admin/realms/insys/users/profile", admin);
        var tenantId = profile.GetProperty("attributes").EnumerateArray().Single(a => a.GetProperty("name").GetString() == "tenant_id");
        tenantId.GetProperty("permissions").GetProperty("edit").EnumerateArray().Select(e => e.GetString()).ShouldBe(["admin"]);
        tenantId.GetProperty("permissions").GetProperty("view").EnumerateArray().Select(e => e.GetString()).ShouldBe(["admin"]);

        // The user tries to change their own tenant through the account API; the attribute stays as the administrator set it.
        var own = await Token("insys", "insys-account-test", "insys-admin", DevPassword);
        using var update = new HttpRequestMessage(HttpMethod.Post, $"{Base}/realms/insys/account/")
        {
            Content = JsonContent.Create(new
            {
                username = "insys-admin", email = "insys-admin@example.com", firstName = "Platform", lastName = "Administrator",
                attributes = new Dictionary<string, string[]> { ["tenant_id"] = ["00000000-0000-0000-0000-00000000000b"] },
            }),
        };
        update.Headers.Authorization = new AuthenticationHeaderValue("Bearer", own);
        using var updated = await _http.SendAsync(update);
        updated.StatusCode.ShouldNotBeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden], "the account API must have accepted the user's own token");

        var user = await Get<JsonElement>($"{Base}/admin/realms/insys/users/{AdminSubject}", admin);
        user.GetProperty("attributes").GetProperty("tenant_id")[0].GetString().ShouldBe(OperatorTenant);
    }

    private Task<string> Token(string realmName, string clientId, string username, string password) => Token(_http, Base, realmName, clientId, username, password);

    private static async Task<string> Token(HttpClient http, string baseAddress, string realmName, string clientId, string username, string password)
    {
        var response = await http.PostAsync($"{baseAddress}/realms/{realmName}/protocol/openid-connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password", ["client_id"] = clientId, ["username"] = username, ["password"] = password, ["scope"] = "openid",
        }));
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("access_token").GetString()!;
    }

    private async Task<T> Get<T>(string url, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static JsonElement Payload(string jwt)
    {
        var part = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        return JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(part.PadRight(part.Length + ((4 - (part.Length % 4)) % 4), '=')))).RootElement;
    }

    private static IEnumerable<string?> Audiences(JsonElement claims) =>
        claims.GetProperty("aud").ValueKind == JsonValueKind.Array ? claims.GetProperty("aud").EnumerateArray().Select(a => a.GetString()) : [claims.GetProperty("aud").GetString()];
}
