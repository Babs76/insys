/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Api
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
using System.Security.Cryptography;
using Insys.BuildingBlocks.Application;
using Insys.BuildingBlocks.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.BuildingBlocks.Api;

/// <summary>
/// Requires an Idempotency-Key header on commands that create records or move money (INT-003, PAY-001, CLM-005).
/// The first request runs and its response is stored with a hash of the request; a replay with the same request
/// returns the stored response; the same key with a different request returns 422; a key still running returns 409.
/// </summary>
public sealed class IdempotencyFilter : IEndpointFilter
{
    public const string HeaderName = "Idempotency-Key";
    public const string ReplayedHeader = "Idempotent-Replayed";
    private const int MaxKeyLength = 80;
    private static readonly string[] KeptHeaders = ["Location", "ETag"];

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var key = http.Request.Headers[HeaderName].ToString();
        if (string.IsNullOrWhiteSpace(key) || key.Length > MaxKeyLength)
        {
            return Problems.For(ErrorCodes.IdempotencyKeyRequired, $"This operation requires an {HeaderName} header of 1 to {MaxKeyLength} characters.",
                StatusCodes.Status422UnprocessableEntity);
        }

        var operation = (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? http.Request.Path.ToString();
        operation = $"{http.Request.Method} {operation}";
        var hash = await HashRequest(http.Request);
        var store = http.RequestServices.GetRequiredService<IIdempotencyStore>();
        var begin = await store.Begin(key, operation, hash, http.RequestAborted);

        switch (begin.Outcome)
        {
            case IdempotencyBeginOutcome.KeyReusedWithDifferentRequest:
                return Problems.For(ErrorCodes.IdempotencyKeyReused, "The Idempotency-Key was already used with a different request.",
                    StatusCodes.Status422UnprocessableEntity);
            case IdempotencyBeginOutcome.InProgress:
                return Problems.For(ErrorCodes.IdempotencyInProgress, "A request with this Idempotency-Key is still being processed.",
                    StatusCodes.Status409Conflict);
            case IdempotencyBeginOutcome.Replay when http.GetEndpoint()?.Metadata.GetMetadata<SecretShownOnceMetadata>() is not null:
                return Problems.For(ErrorCodes.IdempotencyResponseNotKept,
                    "This request was already carried out, and its response showed a secret once, which is not kept. Revoke what it issued and issue again.",
                    StatusCodes.Status409Conflict);
            case IdempotencyBeginOutcome.Replay:
                return new StoredResult(begin.Response!);
        }

        IdempotentResponse response;
        try
        {
            var result = await next(context);
            if (http.GetEndpoint()?.Metadata.GetMetadata<ETagResponseMetadata>() is not null)
            {
                RowVersionETagFilter.Apply(http, result);
            }

            response = await Capture(http, result);
        }
        catch
        {
            await store.Release(key, CancellationToken.None);
            throw;
        }

        if (response.StatusCode >= 500)
        {
            await store.Release(key, CancellationToken.None);
        }
        else
        {
            // A secret shown once (a callback endpoint's key and signing secret) is never stored; only that the request ran.
            var kept = http.GetEndpoint()?.Metadata.GetMetadata<SecretShownOnceMetadata>() is null ? response : response with { Body = null };
            await store.Complete(key, kept, CancellationToken.None);
        }

        return new StoredResult(response, replayed: false);
    }

    private static async Task<string> HashRequest(HttpRequest request)
    {
        // The body was buffered by IdempotencyBufferingMiddleware before parameter binding read it.
        if (!request.Body.CanSeek)
        {
            throw new InvalidOperationException("Idempotent endpoints need IdempotencyBufferingMiddleware (UseInsysApi adds it).");
        }

        request.Body.Position = 0;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(System.Text.Encoding.UTF8.GetBytes($"{request.Method}\n{request.Path}\n{request.QueryString}\n"));
        var buffer = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(buffer, request.HttpContext.RequestAborted)) > 0)
        {
            sha.AppendData(buffer, 0, read);
        }

        request.Body.Position = 0;
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    /// <summary>Executes the endpoint's result into a buffer so the exact response can be stored and replayed.</summary>
    private static async Task<IdempotentResponse> Capture(HttpContext http, object? result)
    {
        var original = http.Response.Body;
        await using var buffer = new MemoryStream();
        http.Response.Body = buffer;
        try
        {
            switch (result)
            {
                case IResult httpResult:
                    await httpResult.ExecuteAsync(http);
                    break;
                case null:
                    break;
                default:
                    await Results.Ok(result).ExecuteAsync(http);
                    break;
            }
        }
        finally
        {
            http.Response.Body = original;
        }

        var headers = KeptHeaders
            .Where(h => http.Response.Headers.ContainsKey(h))
            .ToDictionary(h => h, h => http.Response.Headers[h].ToString());
        var body = buffer.Length == 0 ? null : System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        return new IdempotentResponse(http.Response.StatusCode, http.Response.ContentType, body, headers);
    }

    private sealed class StoredResult(IdempotentResponse response, bool replayed = true) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = response.StatusCode;
            foreach (var (name, value) in response.Headers)
            {
                httpContext.Response.Headers[name] = value;
            }

            if (replayed)
            {
                httpContext.Response.Headers[ReplayedHeader] = "true";
            }

            if (response.Body is not null)
            {
                httpContext.Response.ContentType = response.ContentType;
                await httpContext.Response.WriteAsync(response.Body);
            }
        }
    }
}

public static class IdempotencyEndpointExtensions
{
    public static TBuilder RequireIdempotencyKey<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter<TBuilder, IdempotencyFilter>().WithMetadata(new IdempotentEndpointMetadata());

    /// <summary>The response shows a secret once: it is not kept for repeats, which are refused (409 IDEMPOTENCY_RESPONSE_NOT_KEPT).</summary>
    public static TBuilder ShowsSecretOnce<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder => builder.WithMetadata(new SecretShownOnceMetadata());
}

public sealed record IdempotentEndpointMetadata;

/// <summary>The response shows a secret once (ADR-023): the idempotency store keeps no body, and a repeat is refused with IDEMPOTENCY_RESPONSE_NOT_KEPT.</summary>
public sealed record SecretShownOnceMetadata;

/// <summary>Buffers the request body of idempotent endpoints so the filter can hash it after model binding.</summary>
public sealed class IdempotencyBufferingMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<IdempotentEndpointMetadata>() is not null)
        {
            context.Request.EnableBuffering();
        }

        return next(context);
    }
}
