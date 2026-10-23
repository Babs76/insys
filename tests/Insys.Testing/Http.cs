/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Testing
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
using System.Net.Http.Json;
using System.Text.Json;

namespace Insys.Testing;

/// <summary>Request helpers for API tests.</summary>
public static class Http
{
    public static async Task<HttpResponseMessage> Send(this HttpClient client, HttpMethod method, string url, object? body = null, string? ifMatch = null,
        bool idempotent = false)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (idempotent)
        {
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        }

        return await client.SendAsync(request);
    }

    public static async Task<JsonElement> Json(this HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    public static async Task<string?> Code(this HttpResponseMessage response) =>
        (await response.Json()).TryGetProperty("code", out var code) ? code.GetString() : null;

    public static string ETag(this HttpResponseMessage response) => response.Headers.ETag?.Tag ?? throw new InvalidOperationException("No ETag.");
}
