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
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Insys.Seed;

/// <summary>A refusal from the API, which stops the seed where it is: it writes nothing around one.</summary>
public sealed class SeedRefusedException(string what, HttpStatusCode status, string body) : Exception($"{what}: {(int)status} {body}")
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>Calls the API as one persona. The seed only ever goes through the API, as any user would (never the database).</summary>
public sealed class Persona(HttpClient http, Action<string>? afterCall = null)
{
    public HttpClient Http { get; } = http;

    /// <summary>Waits for what earlier writes raised to be applied (the CLI: until /health shows no pending outbox event), before a 412's re-read.</summary>
    public Func<Task>? Settle { get; set; }

    /// <summary>How many times a write refused with 412 is tried again on the record's fresh version.</summary>
    public const int PreconditionRetries = 3;

    /// <param name="key">The Idempotency-Key, deterministic so a rerun gets the original record back: the tenant code, the record's type and its business key. By default, a hash of the call itself.</param>
    public async Task<JsonElement> Send(HttpMethod method, string path, object? body = null, string? ifMatch = null, bool idempotent = true, string? key = null)
    {
        string? retryTag = null;   // the version a 412's retry is made on: in its Idempotency-Key, as a route that requires one keeps its 412 against the first
        HttpRequestMessage Request()
        {
            var request = new HttpRequestMessage(method, path);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body, options: JsonSerializerOptions.Web);
            }

            if (idempotent && method == HttpMethod.Post)
            {
                // The API takes at most 80 characters, so a long business key goes in as its hash.
                var source = (key ?? path + "|" + JsonSerializer.Serialize(body, JsonSerializerOptions.Web)) + (retryTag is null ? "" : "|retry " + retryTag);
                var plain = key is null ? null : retryTag is null ? key : $"{key}:r{retryTag}";
                request.Headers.Add("Idempotency-Key", plain is { Length: <= 80 } ? plain : $"seed:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..40]}");
            }

            if (ifMatch is not null)
            {
                request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
            }

            return request;
        }

        var response = await Paced(Request);
        var text = await response.Content.ReadAsStringAsync();
        // Asked for the record's version, as a client that has just read it would: the record is the path, or the path less its action.
        if (response.StatusCode == HttpStatusCode.PreconditionRequired && ifMatch is null)
        {
            ifMatch = await CurrentVersion(path);
            if (ifMatch is not null)
            {
                response.Dispose();
                response = await Paced(Request);
                text = await response.Content.ReadAsStringAsync();
            }
        }
        else if (response.StatusCode == HttpStatusCode.UnprocessableEntity && !idempotent && text.Contains("IDEMPOTENCY_KEY_REQUIRED", StringComparison.Ordinal))
        {
            idempotent = true;
            response.Dispose();
            response = await Paced(Request);
            text = await response.Content.ReadAsStringAsync();
        }

        // Refused because the record changed since its version was read: a consumer of an earlier write's event, through the relay
        // (rotate-passwords run 42057678256: a party's contact point, after IdentityVerificationPolicy handled PartyIdentifierAdded). A 412
        // means nothing was applied, so the write is made again on the record's fresh version, after a settle, under a key of its own (a route
        // that requires an Idempotency-Key keeps a 4xx against it for 24 hours, and would replay it).
        for (var retry = 1; response.StatusCode == HttpStatusCode.PreconditionFailed && ifMatch is not null && retry <= PreconditionRetries; retry++)
        {
            if (Settle is not null)
            {
                await Settle();
            }

            var fresh = await CurrentVersion(path);
            if (fresh is null)
            {
                break;
            }

            ifMatch = fresh;
            retryTag = $"{retry}-{fresh.Trim('"')}";
            response.Dispose();
            response = await Paced(Request);
            text = await response.Content.ReadAsStringAsync();
        }

        using var answered = response;
        afterCall?.Invoke($"{method} {path}");
        if (!response.IsSuccessStatusCode)
        {
            throw new SeedRefusedException($"{method} {path}", response.StatusCode, text);
        }

        return text.Length == 0 ? default : JsonSerializer.Deserialize<JsonElement>(text);
    }

    /// <summary>The record's version: the path itself, or up to three segments up (an action, or an action on a sub-resource such as a tenant's smoke run).</summary>
    private async Task<string?> CurrentVersion(string path)
    {
        var candidates = new List<string> { path };
        for (var up = path; candidates.Count < 4 && up.LastIndexOf('/') > 0; candidates.Add(up))
        {
            up = up[..up.LastIndexOf('/')];
        }

        foreach (var candidate in candidates)
        {
            using var current = await Paced(() => new HttpRequestMessage(HttpMethod.Get, candidate));
            if (current.IsSuccessStatusCode && current.Headers.ETag?.Tag is { } tag)
            {
                return tag;
            }
        }

        return null;
    }

    public async Task<bool> Exists(string path)
    {
        using var response = await Paced(() => new HttpRequestMessage(HttpMethod.Get, path));
        afterCall?.Invoke($"GET {path}");
        return response.IsSuccessStatusCode;
    }

    /// <summary>The seed keeps to the API's rate limit as any client should: told to slow down, it waits and asks again.</summary>
    private async Task<HttpResponseMessage> Paced(Func<HttpRequestMessage> request)
    {
        var backOff = TimeSpan.FromSeconds(5);
        for (var attempt = 1; ; attempt++)
        {
            using var message = request();
            var response = await Http.SendAsync(message);
            if (response.StatusCode != HttpStatusCode.TooManyRequests || attempt == 30)
            {
                return response;
            }

            // The API's Retry-After says when; without one, each wait doubles, up to a minute.
            var wait = response.Headers.RetryAfter?.Delta ?? backOff;
            backOff = TimeSpan.FromSeconds(Math.Min(backOff.TotalSeconds * 2, 60));
            response.Dispose();
            await Task.Delay(wait);
        }
    }
}

/// <summary>What the seed runs with: who it acts as, and how to wait for events to be delivered, and, where it can be asked, how to run the daily jobs now (elsewhere their timers do). OwnRealm: users are made in INSYS's own Keycloak realm, which signs them in (KeycloakSignIn). Accounts: the INSYS Preview's generic accounts too.</summary>
public sealed record SeedContext(Persona PlatformMaker, Persona PlatformChecker, Func<Guid, string, Persona> Tenant, Func<Task> Settle, Func<Task>? Jobs = null, bool OwnRealm = false, bool Accounts = false,
    int PendingWaitSeconds = 120);

/// <summary>A checker's step found nothing to decide within the wait: what the maker recorded never reached it.</summary>
public sealed class SeedPendingTimeoutException(string message) : Exception(message);
