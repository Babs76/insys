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
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Insys.General.Contracts;
using Insys.Testing;
using Testcontainers.Keycloak;

namespace Insys.IntegrationTests;

/// <summary>
/// The local identity provider (ADR-017, Phase 8 Workstream D) from the committed realm export: tokens carry sub and
/// tenant_id and the API's audience, and nothing else about the user; the API accepts them; and tenant_id can be changed
/// by an administrator only, never by the user through the account console. A broker's token keeps its second factor
/// when refreshed (BR-094).
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

    /// <summary>
    /// BR-094: the realm's method references (amr-pwd, amr-otp) give each method a maximum age, so a broker who signed in
    /// with a password and a one-time code still shows both in a refreshed token, and INSYS accepts it. Without one,
    /// Keycloak's AMR mapper uses 0 seconds and a refreshed token has no amr, which INSYS refuses with PORTAL_MFA_REQUIRED.
    /// </summary>
    [Fact]
    public async Task ARefreshedBrokerToken_StillCarriesTheOneTimeCode_AndIsAccepted_BR094()
    {
        var tenant = DatabaseFixture.TenantA;
        var subject = Guid.NewGuid().ToString();
        var password = $"pw-{Guid.NewGuid():N}";
        var secret = Guid.NewGuid().ToString("N");
        var admin = await Token("master", "admin-cli", "admin", "admin");
        using (var import = new HttpRequestMessage(HttpMethod.Post, $"{Base}/admin/realms/insys/partialImport")
               {
                   Content = JsonContent.Create(new
                   {
                       ifResourceExists = "FAIL",
                       users = new object[]
                       {
                           new
                           {
                               id = subject, username = "broker-94", enabled = true, email = "broker-94@example.com", firstName = "Broker", lastName = "Ninety-Four",
                               attributes = new Dictionary<string, string[]> { ["tenant_id"] = [tenant.ToString()] },
                               credentials = new object[]
                               {
                                   new { type = "password", value = password, temporary = false },
                                   new
                                   {
                                       type = "otp", userLabel = "test",
                                       secretData = JsonSerializer.Serialize(new { value = secret }),
                                       credentialData = JsonSerializer.Serialize(new { subType = "totp", digits = 6, counter = 0, period = 30, algorithm = "HmacSHA1" }),
                                   },
                               },
                           },
                       },
                   }),
               })
        {
            import.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
            using var imported = await _http.SendAsync(import);
            imported.StatusCode.ShouldBe(HttpStatusCode.OK, await imported.Content.ReadAsStringAsync());
        }

        // INSYS: the broker channel names the broker portal, and the Keycloak user is linked as broker B94.
        await fixture.Database.SeedStatic(tenant, "channel.cfg_channel_definitions", "EFFECTIVE", "CH-BRK94", new DateOnly(2026, 1, 1), ("channel_code", "BRK94"),
            ("audience", "BROKER"), ("name", "Broker portal"), ("client_ids", new[] { "broker-portal" }));
        var user = await fixture.Database.UserWithPermissions(tenant, subject, GeneralPermissions.PolicyRead);
        await fixture.Database.ExecuteAsOwner("""
            INSERT INTO iam.user_subject_link (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, user_id, kind, party_id, intermediary_code,
                                               provider_id, status, requested_by, decided_by, decided_at)
            VALUES (@t, gen_random_uuid(), 1, now(), 'seed', now(), 'seed', @u, 'INTERMEDIARY', NULL, 'B94', NULL, 'ACTIVE', gen_random_uuid(), gen_random_uuid(), now())
            """, ("t", tenant), ("u", user));

        // The broker portal's own flow: Authorization Code with PKCE, the password form, then the one-time code form.
        using var browser = new HttpClient(new BrowserCookies());
        const string redirect = "http://localhost:5173/callback";
        var verifier = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var page = await browser.GetStringAsync($"{Base}/realms/insys/protocol/openid-connect/auth?client_id=broker-portal&response_type=code&scope=openid"
                                                + $"&redirect_uri={Uri.EscapeDataString(redirect)}&code_challenge={challenge}&code_challenge_method=S256&state=br094");
        using var signedIn = await browser.PostAsync(FormAction(page), new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = "broker-94", ["password"] = password }));
        signedIn.StatusCode.ShouldBe(HttpStatusCode.OK, "the one-time code form follows the password");
        using var coded = await browser.PostAsync(FormAction(await signedIn.Content.ReadAsStringAsync()),
            new FormUrlEncodedContent(new Dictionary<string, string> { ["otp"] = Totp(secret) }));
        coded.StatusCode.ShouldBe(HttpStatusCode.Found, await coded.Content.ReadAsStringAsync());
        var code = Regex.Match(coded.Headers.Location!.Query, "[?&]code=([^&]+)").Groups[1].Value;

        var tokens = await TokenResponse(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["client_id"] = "broker-portal", ["code"] = code, ["redirect_uri"] = redirect, ["code_verifier"] = verifier,
        });
        Methods(tokens.GetProperty("access_token").GetString()!).ShouldBe(["otp", "pwd"], ignoreOrder: true);

        // Later (a reload or the access token's expiry), the portal refreshes; the methods are still within their maximum age.
        await Task.Delay(TimeSpan.FromSeconds(2));
        var refreshed = await TokenResponse(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token", ["client_id"] = "broker-portal", ["refresh_token"] = tokens.GetProperty("refresh_token").GetString()!,
        });
        var access = refreshed.GetProperty("access_token").GetString()!;
        Methods(access).ShouldContain("otp", "a refreshed broker token keeps its second factor");

        var factory = await fixture.Host(new Dictionary<string, string?>
        {
            ["Authentication:Authority"] = $"{Base}/realms/insys", ["Authentication:Issuer"] = null, ["Authentication:Audience"] = "insys-core-api",
            ["Authentication:RequireHttpsMetadata"] = "false", ["Authentication:DevelopmentSigningKey"] = "",
        });
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access);
        var response = await client.GetAsync("/api/v1/portal/general/policies");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static string FormAction(string html)
    {
        var action = WebUtility.HtmlDecode(Regex.Match(html, "<form[^>]*action=\"([^\"]+)\"").Groups[1].Value);
        action.ShouldNotBeNullOrEmpty("a Keycloak form");
        return action;
    }

    /// <summary>
    /// Keeps Keycloak's login cookies as a browser does for localhost. They are marked Secure, which .NET's cookie container
    /// will not send over plain HTTP to the test container.
    /// </summary>
    private sealed class BrowserCookies() : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        private readonly Dictionary<string, string> _cookies = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_cookies.Count > 0)
            {
                request.Headers.Add("Cookie", string.Join("; ", _cookies.Select(c => $"{c.Key}={c.Value}")));
            }

            var response = await base.SendAsync(request, cancellationToken);
            foreach (var cookie in response.Headers.TryGetValues("Set-Cookie", out var set) ? set : [])
            {
                var pair = cookie.Split(';')[0].Split('=', 2);
                _cookies[pair[0]] = pair[1];
            }

            return response;
        }
    }

    /// <summary>RFC 6238 with Keycloak's defaults (HMAC-SHA1, 6 digits, 30 seconds), over the stored secret's bytes.</summary>
    private static string Totp(string secret)
    {
        var counter = BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(counter);
        }

        var hash = HMACSHA1.HashData(Encoding.UTF8.GetBytes(secret), counter);
        var offset = hash[^1] & 0x0f;
        var value = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (value % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<JsonElement> TokenResponse(Dictionary<string, string> form)
    {
        var response = await _http.PostAsync($"{Base}/realms/insys/protocol/openid-connect/token", new FormUrlEncodedContent(form));
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement;
    }

    private static string[] Methods(string jwt) =>
        Payload(jwt).TryGetProperty("amr", out var amr) ? [.. amr.EnumerateArray().Select(m => m.GetString()!)] : [];

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
