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
using System.Net;
using System.Text.Json;

namespace Insys.Testing;

/// <summary>
/// Test clients only (BR-031, BR-057, BR-064, BR-071). Flow tests written before General, claims, billing, distribution,
/// Life and Health took If-Match send their changes without it. When such a change is answered 428, it is sent again once
/// with the ETag of the record it changes (read at /api/v1/{module}/{collection}/{id}), and one answered 422
/// IDEMPOTENCY_KEY_REQUIRED again with a new key. Every retried change so proves its record's read gives an ETag. A request
/// marked <see cref="Raw"/> is sent as it is: the sweep that holds the requirements uses that.
/// </summary>
public sealed class TestPreconditionsHandler : DelegatingHandler
{
    public static readonly HttpRequestOptionsKey<bool> Raw = new("insys.raw");

    private static readonly HashSet<string> Modules = ["general", "claims", "billing", "distribution", "life", "health"];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Get || (request.Options.TryGetValue(Raw, out var raw) && raw) || ReadUrl(request.RequestUri) is not { } read)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.PreconditionRequired && !request.Headers.Contains("If-Match"))
        {
            using var get = new HttpRequestMessage(HttpMethod.Get, read);
            Copy(request, get);
            using var current = await base.SendAsync(get, cancellationToken);
            if (current.Headers.ETag is { } etag)
            {
                var retry = Clone(request, body);
                retry.Headers.TryAddWithoutValidation("If-Match", etag.Tag);
                response.Dispose();
                return await base.SendAsync(retry, cancellationToken);
            }
        }
        else if (response.StatusCode == HttpStatusCode.UnprocessableEntity && !request.Headers.Contains("Idempotency-Key")
                 && await Code(response, cancellationToken) == "IDEMPOTENCY_KEY_REQUIRED")
        {
            var retry = Clone(request, body);
            retry.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            response.Dispose();
            return await base.SendAsync(retry, cancellationToken);
        }

        return response;
    }

    /// <summary>The record a change is on: the first three segments after /api/v1 in the modules that take If-Match.</summary>
    private static Uri? ReadUrl(Uri? uri)
    {
        if (uri is null)
        {
            return null;
        }

        var segments = uri.AbsolutePath.Trim('/').Split('/');
        return segments.Length >= 5 && segments[0] == "api" && segments[1] == "v1" && Modules.Contains(segments[2])
            ? new Uri(uri, "/" + string.Join('/', segments[..5]))
            : null;
    }

    private static HttpRequestMessage Clone(HttpRequestMessage request, byte[]? body)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        Copy(request, clone);
        if (body is not null)
        {
            clone.Content = new ByteArrayContent(body);
            foreach (var header in request.Content!.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return clone;
    }

    private static void Copy(HttpRequestMessage from, HttpRequestMessage to)
    {
        foreach (var header in from.Headers)
        {
            to.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    private static async Task<string?> Code(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await response.Content.LoadIntoBufferAsync(cancellationToken);
        try
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return body.RootElement.ValueKind == JsonValueKind.Object && body.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
