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
using System.Text.Json;

namespace Insys.Seed;

/// <summary>A refusal from the API, which stops the seed where it is: it writes nothing around one.</summary>
public sealed class SeedRefusedException(string what, HttpStatusCode status, string body) : Exception($"{what}: {(int)status} {body}");

/// <summary>Calls the API as one persona. The seed only ever goes through the API, as any user would (never the database).</summary>
public sealed class Persona(HttpClient http)
{
    public HttpClient Http { get; } = http;

    public async Task<JsonElement> Send(HttpMethod method, string path, object? body = null, string? ifMatch = null, bool idempotent = true)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonSerializerOptions.Web);
        }

        if (idempotent && method == HttpMethod.Post)
        {
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        }

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        using var response = await Http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new SeedRefusedException($"{method} {path}", response.StatusCode, text);
        }

        return text.Length == 0 ? default : JsonSerializer.Deserialize<JsonElement>(text);
    }

    public async Task<bool> Exists(string path)
    {
        using var response = await Http.GetAsync(path);
        return response.IsSuccessStatusCode;
    }
}

/// <summary>What the seed runs with: who it acts as, and how to wait for events to be delivered.</summary>
public sealed record SeedContext(Persona PlatformMaker, Persona PlatformChecker, Func<Guid, string, Persona> Tenant, Func<Task> Settle);
