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
using System.Text;
using System.Text.Json;
using Insys.Seed;

namespace Insys.IntegrationTests;

/// <summary>
/// How the upgrade check signs in (Ade, 26 November 2026: "the upgrade check must sign in without changing any password on any account"): by Keycloak's
/// admin impersonation and the back office's own Authorization Code flow, against a fake Keycloak that records every call. It sets no password, changes no
/// user, required action or client (insys-seed stays as it is), and ends only the session it opened. The seed's own runs keep the password sign-in.
/// The real Keycloak is in KeycloakRealmTests.
/// </summary>
public sealed class UpgradeCheckSignInTests
{
    private const string UserId = "0199f000-0000-7000-8000-0000000000a1";
    private const string Session = "impersonated-session-1";

    [Fact]
    public void TheUpgradeCheckSignsInByImpersonation_TheSeedsRunsByPassword()
    {
        KeycloakSignIn.ModeFor(upgradeCheck: true).ShouldBe(KeycloakSignInMode.Impersonation);
        KeycloakSignIn.ModeFor(upgradeCheck: false).ShouldBe(KeycloakSignInMode.Password);
    }

    [Fact]
    public async Task Impersonation_GetsTheUsersToken_WithNoWriteToAnyUserCredentialOrClient_AndEndsOnlyItsOwnSession()
    {
        var keycloak = new FakeKeycloak();
        var signIn = SignIn(keycloak);

        (await signIn.Token("insys-admin")).ShouldBe(FakeKeycloak.AccessToken);
        (await signIn.Token("insys-admin")).ShouldBe(FakeKeycloak.AccessToken, "held until it nears its expiry");
        signIn.Sessions.ShouldBe([Session]);
        await signIn.EndImpersonations();
        signIn.Sessions.ShouldBeEmpty();

        keycloak.Calls.ShouldBe([
            "POST realms/master/protocol/openid-connect/token",
            "GET admin/realms/insys/users?username=insys-admin&exact=true",
            "GET admin/realms/insys/clients?clientId=insys-web",
            "POST admin/realms/insys/users/" + UserId + "/impersonation",
            "GET realms/insys/protocol/openid-connect/auth",
            "POST realms/insys/protocol/openid-connect/token",
            "DELETE admin/realms/insys/sessions/" + Session,
        ]);
        keycloak.Calls.ShouldNotContain(c => c.StartsWith("PUT ", StringComparison.Ordinal) || c.Contains("reset-password", StringComparison.Ordinal)
                                              || c.Contains("credentials", StringComparison.Ordinal) || c.Contains("/logout", StringComparison.Ordinal));
        keycloak.AuthCookie.ShouldBe("KEYCLOAK_IDENTITY=identity-cookie; KEYCLOAK_SESSION=session-cookie", "the impersonation's cookies, and only their values");
        keycloak.AuthRedirect.ShouldBe("https://console.test/upgrade-check");
        keycloak.CodeGrant.ShouldBe("authorization_code insys-web code-1 https://console.test/upgrade-check", "the code at the same redirect, with its verifier");
        keycloak.VerifierMatchesChallenge.ShouldBeTrue("PKCE S256");
    }

    [Fact]
    public async Task Impersonation_SetsNoPassword_AndEnablesNoClient()
    {
        var signIn = SignIn(new FakeKeycloak());

        await Should.ThrowAsync<InvalidOperationException>(() => signIn.SetPassword("insys-admin"));
        await Should.ThrowAsync<InvalidOperationException>(() => signIn.Start());
        await Should.ThrowAsync<InvalidOperationException>(() => signIn.Stop());
    }

    [Fact]
    public async Task ASessionKeycloakWontSignIn_IsRefusedWithItsErrorCodeOnly_NeverThePageOrTheCookies()
    {
        var signIn = SignIn(new FakeKeycloak { AuthError = "login_required" });

        var refused = await Should.ThrowAsync<SeedRefusedException>(() => signIn.Token("insys-admin"));
        refused.Message.ShouldContain("login_required");
        refused.Message.ShouldNotContain("identity-cookie");
        refused.Message.ShouldNotContain("session-cookie");
    }

    [Theory]
    [InlineData("""["https://console.test/*"]""", "https://console.test/upgrade-check")]
    [InlineData("""["http://localhost:5173/*"]""", "http://localhost:5173/upgrade-check")]
    [InlineData("""["/relative/*", "https://console.test/callback"]""", "https://console.test/callback")]
    public void TheRedirect_IsOneTheClientRegistered(string uris, string expected) =>
        KeycloakSignIn.RedirectFor(JsonDocument.Parse($$"""{"redirectUris":{{uris}}}""").RootElement).ShouldBe(expected);

    [Fact]
    public void AClientWithNoRedirect_IsRefused() =>
        Should.Throw<InvalidOperationException>(() => KeycloakSignIn.RedirectFor(JsonDocument.Parse("""{"redirectUris":[]}""").RootElement));

    /// <summary>The sign-in against the fake, both its clients going to it (an HttpClient over a plain handler follows no redirect and keeps no cookie).</summary>
    private static KeycloakSignIn SignIn(FakeKeycloak keycloak) =>
        new(new HttpClient(keycloak) { BaseAddress = new Uri("http://keycloak.test/") }, "insys", "admin", "fake-admin-password", KeycloakSignInMode.Impersonation,
            new HttpClient(keycloak) { BaseAddress = new Uri("http://keycloak.test/") });

    /// <summary>Keycloak's answers for the calls the impersonation makes; anything else is 405, so an unexpected write fails the test.</summary>
    private sealed class FakeKeycloak : HttpMessageHandler
    {
        public const string AccessToken = "header.eyJzaWQiOiJpbXBlcnNvbmF0ZWQtc2Vzc2lvbi0xIn0.signature";

        public List<string> Calls { get; } = [];
        public string? AuthCookie { get; private set; }
        public string? AuthRedirect { get; private set; }
        public string? CodeGrant { get; private set; }
        public bool VerifierMatchesChallenge { get; private set; }
        public string? AuthError { get; init; }

        private string? _challenge;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var path = uri.AbsolutePath.TrimStart('/');
            var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
                .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p.ElementAtOrDefault(1) ?? ""));
            Calls.Add($"{request.Method} {path}{(path.StartsWith("admin/", StringComparison.Ordinal) ? uri.Query : "")}");
            switch (request.Method.Method, path)
            {
                case ("POST", "realms/master/protocol/openid-connect/token"):
                    return Json("""{"access_token":"admin-token","expires_in":60}""");
                case ("GET", "admin/realms/insys/users"):
                    return Json($$"""[{"id":"{{UserId}}","username":"insys-admin"}]""");
                case ("GET", "admin/realms/insys/clients"):
                    return Json("""[{"id":"c1","clientId":"insys-web","redirectUris":["https://console.test/*"]}]""");
                case ("POST", "admin/realms/insys/users/" + UserId + "/impersonation"):
                {
                    var response = Json("""{"redirect":"http://keycloak.test/realms/insys/account","sameRealm":false}""");
                    response.Headers.Add("Set-Cookie", "KEYCLOAK_IDENTITY=identity-cookie;Version=1;Path=/realms/insys/;Secure;HttpOnly;SameSite=None");
                    response.Headers.Add("Set-Cookie", "KEYCLOAK_SESSION=session-cookie;Version=1;Path=/realms/insys/;Max-Age=36000;Secure;SameSite=None");
                    return response;
                }

                case ("GET", "realms/insys/protocol/openid-connect/auth"):
                {
                    AuthCookie = request.Headers.TryGetValues("Cookie", out var cookie) ? string.Join("; ", cookie) : null;
                    AuthRedirect = query["redirect_uri"];
                    _challenge = query["code_challenge_method"] == "S256" ? query["code_challenge"] : null;
                    var response = new HttpResponseMessage(HttpStatusCode.Found);
                    response.Headers.Location = new Uri(AuthError is null
                        ? $"{AuthRedirect}?session_state={Session}&code=code-1"
                        : $"{AuthRedirect}?error={AuthError}&error_description=the+session+cookie+identity-cookie");
                    return response;
                }

                case ("POST", "realms/insys/protocol/openid-connect/token"):
                {
                    var form = (await request.Content!.ReadAsStringAsync(cancellationToken)).Split('&').Select(p => p.Split('=', 2))
                        .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
                    CodeGrant = $"{form["grant_type"]} {form["client_id"]} {form["code"]} {form["redirect_uri"]}";
                    var computed = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"])))
                        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
                    VerifierMatchesChallenge = computed == _challenge;
                    return Json($$"""{"access_token":"{{AccessToken}}","expires_in":300,"session_state":"{{Session}}"}""");
                }

                case ("DELETE", "admin/realms/insys/sessions/" + Session):
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                default:
                    return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
            }
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
