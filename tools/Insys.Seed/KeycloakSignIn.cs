/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Seed
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
using System.Text.Json;

namespace Insys.Seed;

/// <summary>
/// How the seed signs in where the API takes only Keycloak's tokens (the INSYS Preview): as each persona, by the password grant on the realm's own
/// client <c>insys-seed</c>. That client is disabled in the realm; the seed enables it for its run and disables it after. Through Keycloak's admin API it
/// sets a generated password on each account it signs in as and clears its required actions; the passwords stay in memory, so the accounts' real
/// passwords are set after the seed (the Preview's password step).
/// The upgrade check signs in another way (<see cref="KeycloakSignInMode.Impersonation"/>, Ade, 26 November 2026: no password changed on any account).
/// </summary>
/// <param name="browser">For impersonation: the client that reads Keycloak's cookies and redirect, which must follow no redirect and keep no cookie; by default
/// one made so, at <paramref name="keycloak"/>'s address.</param>
public sealed class KeycloakSignIn(HttpClient keycloak, string realm, string adminUser, string adminPassword, KeycloakSignInMode mode = KeycloakSignInMode.Password,
    HttpClient? browser = null)
{
    public const string ClientId = "insys-seed";

    /// <summary>The client the upgrade check's impersonated session signs in through: the back office's own (Authorization Code with PKCE), unchanged.</summary>
    public const string ImpersonationClientId = "insys-web";

    private readonly Dictionary<string, (string Token, DateTimeOffset Expires)> _tokens = new(StringComparer.Ordinal);
    private readonly List<string> _sessions = [];
    private (string Token, DateTimeOffset Expires) _admin;

    // Keycloak's sign-in cookies and its redirect with the code are read, never followed or kept: no cookie jar, no automatic redirect.
    private readonly HttpClient _browser = browser ?? new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = keycloak.BaseAddress };

    public KeycloakSignInMode Mode { get; } = mode;

    /// <summary>
    /// How a run signs in: the upgrade check by impersonation, which changes no credential and no client (Ade, 26 November 2026); the seed's own runs by
    /// password, as before.
    /// </summary>
    public static KeycloakSignInMode ModeFor(bool upgradeCheck) => upgradeCheck ? KeycloakSignInMode.Impersonation : KeycloakSignInMode.Password;

    /// <summary>The Keycloak sessions this run's impersonations made, by id: ended by <see cref="EndImpersonations"/>.</summary>
    public IReadOnlyList<string> Sessions => _sessions;

    /// <summary>The tenant's Keycloak username: Identity names users of INSYS's own realm "&lt;tenant code&gt;.&lt;username&gt;", in lower case.</summary>
    public static string Username(string tenantCode, string username) => $"{tenantCode}.{username}".ToLowerInvariant();

    public Task Start() => Mode == KeycloakSignInMode.Password ? SetClientEnabled(true) : throw new InvalidOperationException("Impersonation enables no client.");

    public Task Stop() => Mode == KeycloakSignInMode.Password ? SetClientEnabled(false) : throw new InvalidOperationException("Impersonation enables no client.");

    /// <summary>A handler that puts the persona's token on each call, signing in again shortly before it expires.</summary>
    public DelegatingHandler For(string username) => new Bearer(this, username);

    public async Task<string> Token(string username)
    {
        if (_tokens.TryGetValue(username, out var held) && held.Expires > DateTimeOffset.UtcNow.AddSeconds(30))
        {
            return held.Token;
        }

        if (Mode == KeycloakSignInMode.Impersonation)
        {
            var impersonated = await Impersonate(username);
            _tokens[username] = impersonated;
            return impersonated.Token;
        }

        var password = await SetPassword(username);
        var token = await Grant($"realms/{realm}/protocol/openid-connect/token", new Dictionary<string, string>
        {
            ["grant_type"] = "password", ["client_id"] = ClientId, ["username"] = username, ["password"] = password, ["scope"] = "openid",
        });
        _tokens[username] = token;
        return token.Token;
    }

    /// <summary>A new generated password on the account, with its required actions cleared so it can sign in; returned, never logged.</summary>
    public async Task<string> SetPassword(string username)
    {
        if (Mode == KeycloakSignInMode.Impersonation)
        {
            throw new InvalidOperationException("The upgrade check changes no password.");
        }

        var user = await User(username);
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        await Admin(HttpMethod.Put, $"users/{user}/reset-password", new { type = "password", value = password, temporary = false });
        await Admin(HttpMethod.Put, $"users/{user}", new { requiredActions = Array.Empty<string>(), emailVerified = true });
        return password;
    }

    /// <summary>
    /// CONFIGURE_TOTP required at the account's next sign-in, so its user sets up an authenticator themselves (the demo accounts, Ade, 23 November
    /// 2026). Left alone once the account has a one-time code credential, so a later password rotation never makes its user enrol again.
    /// </summary>
    public async Task RequireTotpSetUp(string username)
    {
        var user = await User(username);
        if ((await Admin(HttpMethod.Get, $"users/{user}/credentials")).EnumerateArray().Any(c => c.GetProperty("type").GetString() == "otp"))
        {
            return;
        }

        await Admin(HttpMethod.Put, $"users/{user}", new { requiredActions = new[] { "CONFIGURE_TOTP" } });
    }

    /// <summary>The user's id. Identity creates a tenant's users in Keycloak when it delivers their event, so a new one may take a moment to appear.</summary>
    private async Task<string> User(string username)
    {
        for (var attempt = 0; ; attempt++)
        {
            var found = await Admin(HttpMethod.Get, $"users?username={Uri.EscapeDataString(username)}&exact=true");
            if (found.GetArrayLength() > 0)
            {
                return found[0].GetProperty("id").GetString()!;
            }

            if (attempt == 30)
            {
                throw new InvalidOperationException($"Keycloak has no user {username}.");
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>
    /// The user's token without their password: Keycloak's admin impersonation (<c>POST /admin/realms/{realm}/users/{id}/impersonation</c>, the
    /// <c>impersonation</c> feature, on by default in Keycloak 26) opens a session for the user and answers with its sign-in cookies; the back office's
    /// client then takes that session through its own Authorization Code flow with PKCE, to one of its registered redirect addresses, which is read here
    /// and never called. Nothing is written to the user, its credentials or required actions, or to any client: the session is the only thing made, and
    /// <see cref="EndImpersonations"/> ends it. The cookies, the code and the token are never logged.
    /// </summary>
    private async Task<(string Token, DateTimeOffset Expires)> Impersonate(string username)
    {
        var user = await User(username);
        var redirect = RedirectFor((await Admin(HttpMethod.Get, $"clients?clientId={ImpersonationClientId}"))[0]);

        string cookies;
        using (var request = new HttpRequestMessage(HttpMethod.Post, $"admin/realms/{realm}/users/{user}/impersonation"))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AdminToken());
            using var response = await _browser.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                throw new SeedRefusedException($"Keycloak impersonation of {username}", response.StatusCode, await response.Content.ReadAsStringAsync());
            }

            cookies = string.Join("; ", (response.Headers.TryGetValues("Set-Cookie", out var set) ? set : []).Select(c => c.Split(';')[0]).Where(c => c.Contains('=')));
            if (cookies.Length == 0)
            {
                throw new InvalidOperationException($"Keycloak's impersonation of {username} set no session cookie.");
            }
        }

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier)));
        string code;
        using (var request = new HttpRequestMessage(HttpMethod.Get, $"realms/{realm}/protocol/openid-connect/auth?client_id={ImpersonationClientId}&response_type=code"
                                                                    + $"&scope=openid&redirect_uri={Uri.EscapeDataString(redirect)}&code_challenge={challenge}&code_challenge_method=S256"))
        {
            request.Headers.Add("Cookie", cookies);
            using var response = await _browser.SendAsync(request);
            var location = response.Headers.Location;
            var query = Query(location is null ? "" : (location.IsAbsoluteUri ? location : new Uri(new Uri(redirect), location)).Query);
            if (response.StatusCode != HttpStatusCode.Found || query.GetValueOrDefault("code") is not { Length: > 0 } issued)
            {
                // Keycloak's error code only (login_required, a required action's form): never the page, the address or the cookies.
                throw new SeedRefusedException($"Keycloak sign-in as {username} by impersonation", response.StatusCode, query.GetValueOrDefault("error") ?? "no code was issued");
            }

            code = issued;
        }

        using var granted = await _browser.PostAsync($"realms/{realm}/protocol/openid-connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["client_id"] = ImpersonationClientId, ["code"] = code, ["redirect_uri"] = redirect, ["code_verifier"] = verifier,
        }));
        var text = await granted.Content.ReadAsStringAsync();
        if (!granted.IsSuccessStatusCode)
        {
            throw new SeedRefusedException($"Keycloak sign-in as {username} by impersonation", granted.StatusCode, text);
        }

        var json = JsonSerializer.Deserialize<JsonElement>(text);
        var access = json.GetProperty("access_token").GetString()!;
        if ((json.TryGetProperty("session_state", out var state) ? state.GetString() : SessionOf(access)) is { Length: > 0 } id && !_sessions.Contains(id))
        {
            _sessions.Add(id);
        }

        return (access, DateTimeOffset.UtcNow.AddSeconds(json.GetProperty("expires_in").GetInt32()));
    }

    /// <summary>Ends the sessions this run's impersonations made, each by its id, and no other session of the user's (not the user's own sign-ins).</summary>
    public async Task EndImpersonations()
    {
        foreach (var session in _sessions.ToArray())
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, $"admin/realms/{realm}/sessions/{Uri.EscapeDataString(session)}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AdminToken());
            using var response = await _browser.SendAsync(request);
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
            {
                throw new SeedRefusedException("Keycloak DELETE sessions", response.StatusCode, await response.Content.ReadAsStringAsync());
            }

            _sessions.Remove(session);
        }

        _tokens.Clear();
    }

    /// <summary>One of the client's registered redirect addresses, as Keycloak matches it: a trailing wildcard stands for any path under it.</summary>
    public static string RedirectFor(JsonElement client)
    {
        var registered = client.TryGetProperty("redirectUris", out var uris) && uris.ValueKind == JsonValueKind.Array
            ? uris.EnumerateArray().Select(u => u.GetString()).FirstOrDefault(u => u is not null && (u.StartsWith("https://", StringComparison.Ordinal) || u.StartsWith("http://", StringComparison.Ordinal)))
            : null;
        if (registered is null)
        {
            throw new InvalidOperationException($"{ImpersonationClientId} has no registered redirect address.");
        }

        return registered.EndsWith('*') ? registered[..^1].TrimEnd('/') + "/upgrade-check" : registered;
    }

    /// <summary>The token's session (its <c>sid</c> claim).</summary>
    private static string? SessionOf(string jwt)
    {
        var part = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        var payload = JsonSerializer.Deserialize<JsonElement>(Convert.FromBase64String(part.PadRight(part.Length + ((4 - (part.Length % 4)) % 4), '=')));
        return payload.TryGetProperty("sid", out var sid) ? sid.GetString() : null;
    }

    private static Dictionary<string, string> Query(string query) =>
        query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
            .GroupBy(p => Uri.UnescapeDataString(p[0]), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => Uri.UnescapeDataString(g.First().ElementAtOrDefault(1) ?? ""), StringComparer.Ordinal);

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private async Task SetClientEnabled(bool enabled)
    {
        var client = (await Admin(HttpMethod.Get, $"clients?clientId={ClientId}"))[0];
        var representation = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(client.GetRawText())!;
        representation["enabled"] = JsonSerializer.SerializeToElement(enabled);
        await Admin(HttpMethod.Put, $"clients/{client.GetProperty("id").GetString()}", representation);
    }

    /// <summary>The deployment's Keycloak administrator's token, by its existing password: read, never changed.</summary>
    private async Task<string> AdminToken()
    {
        if (_admin.Expires <= DateTimeOffset.UtcNow.AddSeconds(30))
        {
            _admin = await Grant("realms/master/protocol/openid-connect/token", new Dictionary<string, string>
            {
                ["grant_type"] = "password", ["client_id"] = "admin-cli", ["username"] = adminUser, ["password"] = adminPassword,
            });
        }

        return _admin.Token;
    }

    private async Task<JsonElement> Admin(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, $"admin/realms/{realm}/{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AdminToken());
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await keycloak.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            // Keycloak's answer, never the request: it may carry a password.
            throw new SeedRefusedException($"Keycloak {method} {path.Split('?')[0]}", response.StatusCode, text);
        }

        return text.Length == 0 ? default : JsonSerializer.Deserialize<JsonElement>(text);
    }

    private async Task<(string Token, DateTimeOffset Expires)> Grant(string path, Dictionary<string, string> form)
    {
        using var response = await keycloak.PostAsync(path, new FormUrlEncodedContent(form));
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new SeedRefusedException($"Keycloak sign-in as {form["username"]}", response.StatusCode, text);
        }

        var json = JsonSerializer.Deserialize<JsonElement>(text);
        return (json.GetProperty("access_token").GetString()!, DateTimeOffset.UtcNow.AddSeconds(json.GetProperty("expires_in").GetInt32()));
    }

    private sealed class Bearer(KeycloakSignIn signIn, string username) : DelegatingHandler(new HttpClientHandler())
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await signIn.Token(username));
            return await base.SendAsync(request, cancellationToken);
        }
    }
}

/// <summary>
/// How <see cref="KeycloakSignIn"/> gets a user's token. Password: the seed's runs, a generated password set on the account and the password grant on
/// <c>insys-seed</c>, enabled for the run. Impersonation: the upgrade check, the deployment's Keycloak administrator impersonating the user, so no password
/// or other credential is set and no client is enabled (Ade, 26 November 2026).
/// </summary>
public enum KeycloakSignInMode
{
    Password,
    Impersonation,
}
