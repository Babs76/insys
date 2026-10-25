/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Identity
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
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Insys.Identity.Infrastructure.Keycloak;

/// <summary>
/// The Keycloak admin API for INSYS's own realm (keycloak-user-provisioning §3): the confidential client insys-user-admin,
/// client credentials only. The secret comes from configuration (the secret store in the cloud, user secrets locally).
/// </summary>
public sealed class KeycloakOptions
{
    /// <summary>Keycloak's base address, for example http://localhost:8080. Empty: INSYS creates no Keycloak users, and requests park.</summary>
    public string BaseUrl { get; set; } = "";

    public string Realm { get; set; } = "insys";

    public string ClientId { get; set; } = "insys-user-admin";

    public string ClientSecret { get; set; } = "";

    /// <summary>How long the invitation link is valid. Keycloak's own default for admin-sent links is 12 hours.</summary>
    public TimeSpan InvitationLifespan { get; set; } = TimeSpan.FromHours(72);

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>Keycloak could not be reached, or answered with a server error.</summary>
public sealed class KeycloakUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Keycloak refused a user because another has its username or email (409).</summary>
public sealed class KeycloakConflictException(string field) : Exception($"Keycloak already has a user with this {field}.")
{
    public string Field { get; } = field;
}

/// <summary>
/// Keycloak refused the request itself (a 4xx other than 401, 409 and the retryable 408 and 429): retrying the same request
/// cannot succeed (BR-006). <see cref="Reason"/> is Keycloak's errorMessage, or its status.
/// </summary>
public sealed class KeycloakRefusedException(int status, string reason) : Exception($"Keycloak refused the request ({status}): {reason}")
{
    public string Reason { get; } = reason;
}

/// <summary>Keycloak could not send an email (its SMTP settings, or the mail server).</summary>
public sealed class KeycloakEmailException(string message) : Exception(message);

public sealed record KeycloakUser(string Id, string Username, string? TenantId, bool Enabled);

public interface IKeycloakAdmin
{
    Task<KeycloakUser?> FindByUsername(string username, CancellationToken cancellationToken);

    /// <summary>Creates the user, enabled and with the tenant_id attribute; returns its id (the token's sub).</summary>
    Task<string> Create(string username, string email, string firstName, string lastName, Guid tenantId, CancellationToken cancellationToken);

    Task SendActions(string userId, IReadOnlyList<string> actions, CancellationToken cancellationToken);

    Task SetEnabled(string userId, bool enabled, CancellationToken cancellationToken);

    /// <summary>Ends the user's sessions.</summary>
    Task Logout(string userId, CancellationToken cancellationToken);
}

/// <summary>
/// The admin client. Its access token is cached in memory until shortly before it expires; the token and the secret are
/// never logged, returned or audited.
/// </summary>
internal sealed class KeycloakAdmin(IHttpClientFactory clients, IOptions<KeycloakOptions> options) : IKeycloakAdmin
{
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private (string Token, DateTimeOffset Expires)? _token;

    private KeycloakOptions Settings => options.Value;

    private string Admin => $"{Settings.BaseUrl.TrimEnd('/')}/admin/realms/{Settings.Realm}";

    public async Task<KeycloakUser?> FindByUsername(string username, CancellationToken cancellationToken)
    {
        using var response = await Send(HttpMethod.Get, $"{Admin}/users?username={Uri.EscapeDataString(username)}&exact=true&briefRepresentation=false", null, cancellationToken);
        var users = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        foreach (var user in users.EnumerateArray())
        {
            if (string.Equals(user.GetProperty("username").GetString(), username, StringComparison.OrdinalIgnoreCase))
            {
                var tenant = user.TryGetProperty("attributes", out var attributes) && attributes.TryGetProperty("tenant_id", out var values) && values.GetArrayLength() > 0
                    ? values[0].GetString()
                    : null;
                return new KeycloakUser(user.GetProperty("id").GetString()!, username, tenant, user.GetProperty("enabled").GetBoolean());
            }
        }

        return null;
    }

    public async Task<string> Create(string username, string email, string firstName, string lastName, Guid tenantId, CancellationToken cancellationToken)
    {
        using var response = await Send(HttpMethod.Post, $"{Admin}/users", new
        {
            username, email, firstName, lastName, enabled = true, emailVerified = false,
            attributes = new Dictionary<string, string[]> { ["tenant_id"] = [tenantId.ToString()] },
        }, cancellationToken);
        return response.Headers.Location?.Segments[^1].TrimEnd('/')
               ?? throw new KeycloakUnavailableException("Keycloak created the user without saying where.");
    }

    public async Task SendActions(string userId, IReadOnlyList<string> actions, CancellationToken cancellationToken)
    {
        var lifespan = (int)Settings.InvitationLifespan.TotalSeconds;
        try
        {
            using var _ = await Send(HttpMethod.Put, $"{Admin}/users/{userId}/execute-actions-email?lifespan={lifespan}", actions, cancellationToken);
        }
        catch (KeycloakUnavailableException e) when (e.Message.Contains("send", StringComparison.OrdinalIgnoreCase))
        {
            throw new KeycloakEmailException(e.Message);
        }
    }

    public async Task SetEnabled(string userId, bool enabled, CancellationToken cancellationToken)
    {
        using var _ = await Send(HttpMethod.Put, $"{Admin}/users/{userId}", new { enabled }, cancellationToken);
    }

    public async Task Logout(string userId, CancellationToken cancellationToken)
    {
        using var _ = await Send(HttpMethod.Post, $"{Admin}/users/{userId}/logout", null, cancellationToken);
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string url, object? body, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Settings.BaseUrl))
        {
            throw new KeycloakUnavailableException("Identity:Keycloak:BaseUrl is not configured.");
        }

        using var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await Token(cancellationToken));
        HttpResponseMessage response;
        try
        {
            response = await Client().SendAsync(request, cancellationToken);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new KeycloakUnavailableException($"Keycloak could not be reached ({e.GetType().Name}).");
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        var status = response.StatusCode;
        response.Dispose();
        if (status == HttpStatusCode.Conflict)
        {
            throw new KeycloakConflictException(detail.Contains("email", StringComparison.OrdinalIgnoreCase) ? "email" : "username");
        }

        if (status == HttpStatusCode.Unauthorized)
        {
            _token = null;
        }

        // A refusal retrying cannot fix: say why, never "unavailable" (BR-006).
        if ((int)status is >= 400 and < 500 && status is not (HttpStatusCode.Unauthorized or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests))
        {
            throw new KeycloakRefusedException((int)status, Reason(detail) ?? ((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        // Keycloak's error text names what failed (for example "Failed to send execute actions email"); it holds no secret.
        throw new KeycloakUnavailableException($"Keycloak answered {(int)status}: {Truncate(detail, 200)}");
    }

    /// <summary>Keycloak's errorMessage (or error) from its JSON error body, if it has one.</summary>
    private static string? Reason(string detail)
    {
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(detail);
            foreach (var name in new[] { "errorMessage", "error" })
            {
                if (json.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object && json.RootElement.TryGetProperty(name, out var value)
                    && value.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    return Truncate(value.GetString()!, 80);
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }

        return null;
    }

    private async Task<string> Token(CancellationToken cancellationToken)
    {
        if (_token is { } cached && cached.Expires > DateTimeOffset.UtcNow)
        {
            return cached.Token;
        }

        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (_token is { } again && again.Expires > DateTimeOffset.UtcNow)
            {
                return again.Token;
            }

            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials", ["client_id"] = Settings.ClientId, ["client_secret"] = Settings.ClientSecret,
            });
            HttpResponseMessage response;
            try
            {
                response = await Client().PostAsync($"{Settings.BaseUrl.TrimEnd('/')}/realms/{Settings.Realm}/protocol/openid-connect/token", form, cancellationToken);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                throw new KeycloakUnavailableException($"Keycloak could not be reached ({e.GetType().Name}).");
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new KeycloakUnavailableException($"Keycloak refused the admin client's credentials ({(int)response.StatusCode}).");
                }

                var token = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
                var lifetime = token.TryGetProperty("expires_in", out var expires) ? expires.GetInt32() : 60;
                _token = (token.GetProperty("access_token").GetString()!, DateTimeOffset.UtcNow.AddSeconds(Math.Max(lifetime - 30, 5)));
                return _token.Value.Token;
            }
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private HttpClient Client()
    {
        var client = clients.CreateClient("keycloak-admin");
        client.Timeout = Settings.Timeout;
        return client;
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];
}
