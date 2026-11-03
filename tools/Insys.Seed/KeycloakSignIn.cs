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
/// </summary>
public sealed class KeycloakSignIn(HttpClient keycloak, string realm, string adminUser, string adminPassword)
{
    public const string ClientId = "insys-seed";

    private readonly Dictionary<string, (string Token, DateTimeOffset Expires)> _tokens = new(StringComparer.Ordinal);
    private (string Token, DateTimeOffset Expires) _admin;

    /// <summary>The tenant's Keycloak username: Identity names users of INSYS's own realm "&lt;tenant code&gt;.&lt;username&gt;", in lower case.</summary>
    public static string Username(string tenantCode, string username) => $"{tenantCode}.{username}".ToLowerInvariant();

    public Task Start() => SetClientEnabled(true);

    public Task Stop() => SetClientEnabled(false);

    /// <summary>A handler that puts the persona's token on each call, signing in again shortly before it expires.</summary>
    public DelegatingHandler For(string username) => new Bearer(this, username);

    public async Task<string> Token(string username)
    {
        if (_tokens.TryGetValue(username, out var held) && held.Expires > DateTimeOffset.UtcNow.AddSeconds(30))
        {
            return held.Token;
        }

        var user = await User(username);
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        await Admin(HttpMethod.Put, $"users/{user}/reset-password", new { type = "password", value = password, temporary = false });
        await Admin(HttpMethod.Put, $"users/{user}", new { requiredActions = Array.Empty<string>(), emailVerified = true });
        var token = await Grant($"realms/{realm}/protocol/openid-connect/token", new Dictionary<string, string>
        {
            ["grant_type"] = "password", ["client_id"] = ClientId, ["username"] = username, ["password"] = password, ["scope"] = "openid",
        });
        _tokens[username] = token;
        return token.Token;
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

    private async Task SetClientEnabled(bool enabled)
    {
        var client = (await Admin(HttpMethod.Get, $"clients?clientId={ClientId}"))[0];
        var representation = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(client.GetRawText())!;
        representation["enabled"] = JsonSerializer.SerializeToElement(enabled);
        await Admin(HttpMethod.Put, $"clients/{client.GetProperty("id").GetString()}", representation);
    }

    private async Task<JsonElement> Admin(HttpMethod method, string path, object? body = null)
    {
        if (_admin.Expires <= DateTimeOffset.UtcNow.AddSeconds(30))
        {
            _admin = await Grant("realms/master/protocol/openid-connect/token", new Dictionary<string, string>
            {
                ["grant_type"] = "password", ["client_id"] = "admin-cli", ["username"] = adminUser, ["password"] = adminPassword,
            });
        }

        using var request = new HttpRequestMessage(method, $"admin/realms/{realm}/{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _admin.Token);
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
