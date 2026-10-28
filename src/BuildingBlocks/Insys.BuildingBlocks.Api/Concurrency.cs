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
using System.Globalization;
using Insys.BuildingBlocks.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

namespace Insys.BuildingBlocks.Api;

/// <summary>ETag and If-Match over row_version (DBA-011).</summary>
public static class Concurrency
{
    public static string ETagFor(long rowVersion) => $"\"{rowVersion.ToString(CultureInfo.InvariantCulture)}\"";

    public static void SetETag(HttpContext context, long rowVersion) => context.Response.Headers.ETag = ETagFor(rowVersion);

    /// <summary>Reads the expected row_version from If-Match. Missing gives 428; unreadable gives 412.</summary>
    public static Result<long> ExpectedVersion(HttpRequest request)
    {
        var header = request.Headers.IfMatch.ToString();
        if (string.IsNullOrWhiteSpace(header))
        {
            return new Error(ErrorCodes.PreconditionRequired, "Updates require an If-Match header with the ETag from a GET.", ErrorKind.PreconditionRequired);
        }

        if (EntityTagHeaderValue.TryParse(header, out var tag) && !tag.IsWeak
            && long.TryParse(tag.Tag.AsSpan().Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out var version))
        {
            return version;
        }

        return Error.PreconditionFailed(ErrorCodes.PreconditionFailed, "If-Match does not match the current version.");
    }

    /// <summary>
    /// If-Match when the operation takes it optionally (an idempotent replacement): null without one, the version with
    /// one, 412 when it cannot be read.
    /// </summary>
    public static Result<long?> OptionalExpectedVersion(HttpRequest request) =>
        string.IsNullOrWhiteSpace(request.Headers.IfMatch.ToString())
            ? (long?)null
            : ExpectedVersion(request) is { IsSuccess: true } read ? read.Value : Error.PreconditionFailed(ErrorCodes.PreconditionFailed, "If-Match is not a version ETag.");

    /// <summary>The check a handler makes after loading the aggregate.</summary>
    public static Result EnsureVersion(long current, long expected) =>
        current == expected
            ? Result.Success()
            : Error.PreconditionFailed(ErrorCodes.PreconditionFailed, "The resource has changed since it was read. Read it again and retry.");
}

/// <summary>Marks an endpoint that requires If-Match (the OpenAPI documents show the header and its 412 and 428 responses).</summary>
public sealed record IfMatchRequiredMetadata;

/// <summary>Marks an idempotent replacement that honours If-Match when given (412 when stale) and applies without it.</summary>
public sealed record IfMatchOptionalMetadata;

public static class ConcurrencyEndpointExtensions
{
    private static readonly string[] ProblemContentTypes = ["application/problem+json"];

    /// <summary>Declares that the endpoint reads If-Match (DBA-011): 428 without it, 412 when it is stale.</summary>
    public static TBuilder RequireIfMatch<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new IfMatchRequiredMetadata(),
            new ProducesResponseTypeMetadata(StatusCodes.Status412PreconditionFailed, typeof(Microsoft.AspNetCore.Mvc.ProblemDetails), ProblemContentTypes),
            new ProducesResponseTypeMetadata(StatusCodes.Status428PreconditionRequired, typeof(Microsoft.AspNetCore.Mvc.ProblemDetails), ProblemContentTypes));

    /// <summary>
    /// The endpoint requires If-Match for the aggregate its command changes, and the unit of work compares it (BR-031, BR-057,
    /// BR-064, BR-071): 428 without it, 412 when it cannot be read or is stale. For commands that do not carry the version.
    /// </summary>
    public static TBuilder TakesIfMatch<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireIfMatch().AddEndpointFilter<TBuilder, IfMatchFilter>();

    /// <summary>
    /// Every read in the group that returns a record with a rowVersion gives it as the ETag, and so does every change that
    /// returns one, so a client always has the If-Match for its next change (BR-031, BR-057, BR-064, BR-071).
    /// </summary>
    public static TBuilder ETagsFromRowVersion<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter<TBuilder, RowVersionETagFilter>().WithMetadata(new ETagResponseMetadata());

    /// <summary>Declares that the endpoint sets the ETag itself (the record's rowVersion), for the contract (BR-041).</summary>
    public static TBuilder ReturnsETag<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new ETagResponseMetadata());

    /// <summary>Declares that the endpoint honours If-Match when given: 412 when it is stale; without it the replacement applies.</summary>
    public static TBuilder AllowIfMatch<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new IfMatchOptionalMetadata(),
            new ProducesResponseTypeMetadata(StatusCodes.Status412PreconditionFailed, typeof(Microsoft.AspNetCore.Mvc.ProblemDetails), ProblemContentTypes));
}

/// <summary>
/// The endpoint's successful responses carry an ETag (the record's rowVersion): the contract declares the header on each
/// 2xx response whose body has a rowVersion (BR-041).
/// </summary>
public sealed record ETagResponseMetadata;

/// <summary>Reads If-Match into the request's preconditions, which the unit of work compares with the changed aggregate.</summary>
public sealed class IfMatchFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var expected = Concurrency.ExpectedVersion(context.HttpContext.Request);
        if (expected.IsFailure)
        {
            return Problems.For(expected.Error);
        }

        context.HttpContext.RequestServices.GetRequiredService<Application.Messaging.RequestPreconditions>().Expect(expected.Value);
        return await next(context);
    }
}

/// <summary>Sets the ETag from the rowVersion of the record a response returns, when it returns one and set none itself.</summary>
public sealed class RowVersionETagFilter : IEndpointFilter
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, System.Reflection.PropertyInfo?> RowVersions = new();

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var result = await next(context);
        var response = context.HttpContext.Response;
        if (result is IValueHttpResult { Value: { } value } && result is IStatusCodeHttpResult { StatusCode: >= 200 and < 300 } && !response.Headers.ContainsKey("ETag")
            && RowVersions.GetOrAdd(value.GetType(), type => type.GetProperty("RowVersion") is { PropertyType: var t } property && t == typeof(long) ? property : null) is { } rowVersion)
        {
            Concurrency.SetETag(context.HttpContext, (long)rowVersion.GetValue(value)!);
        }

        return result;
    }
}
