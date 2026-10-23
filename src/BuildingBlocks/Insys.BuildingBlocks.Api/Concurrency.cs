using System.Globalization;
using Insys.BuildingBlocks.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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

    /// <summary>The check a handler makes after loading the aggregate.</summary>
    public static Result EnsureVersion(long current, long expected) =>
        current == expected
            ? Result.Success()
            : Error.PreconditionFailed(ErrorCodes.PreconditionFailed, "The resource has changed since it was read. Read it again and retry.");
}

/// <summary>Marks an endpoint that requires If-Match (the OpenAPI documents show the header and its 412 and 428 responses).</summary>
public sealed record IfMatchRequiredMetadata;

public static class ConcurrencyEndpointExtensions
{
    private static readonly string[] ProblemContentTypes = ["application/problem+json"];

    /// <summary>Declares that the endpoint reads If-Match (DBA-011): 428 without it, 412 when it is stale.</summary>
    public static TBuilder RequireIfMatch<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new IfMatchRequiredMetadata(),
            new ProducesResponseTypeMetadata(StatusCodes.Status412PreconditionFailed, typeof(Microsoft.AspNetCore.Mvc.ProblemDetails), ProblemContentTypes),
            new ProducesResponseTypeMetadata(StatusCodes.Status428PreconditionRequired, typeof(Microsoft.AspNetCore.Mvc.ProblemDetails), ProblemContentTypes));
}
