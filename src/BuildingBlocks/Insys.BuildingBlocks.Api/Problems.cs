using Insys.BuildingBlocks.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Insys.BuildingBlocks.Api;

/// <summary>Maps errors to RFC 9457 ProblemDetails with a stable "code" extension.</summary>
public static class Problems
{
    public static int StatusFor(ErrorKind kind) => kind switch
    {
        ErrorKind.Validation => StatusCodes.Status422UnprocessableEntity,
        ErrorKind.Conflict => StatusCodes.Status409Conflict,
        ErrorKind.NotFound => StatusCodes.Status404NotFound,
        ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
        ErrorKind.PreconditionFailed => StatusCodes.Status412PreconditionFailed,
        ErrorKind.PreconditionRequired => StatusCodes.Status428PreconditionRequired,
        ErrorKind.Unauthorized => StatusCodes.Status401Unauthorized,
        _ => StatusCodes.Status500InternalServerError,
    };

    public static IResult For(Error error)
    {
        var extensions = new Dictionary<string, object?> { ["code"] = error.Code };
        if (error.Details is not null)
        {
            extensions["errors"] = error.Details;
        }

        return Results.Problem(detail: error.Message, statusCode: StatusFor(error.Kind), title: TitleFor(error.Kind), extensions: extensions);
    }

    public static IResult For(string code, string message, int statusCode) =>
        Results.Problem(detail: message, statusCode: statusCode, extensions: new Dictionary<string, object?> { ["code"] = code });

    private static string TitleFor(ErrorKind kind) => kind switch
    {
        ErrorKind.Validation => "The request is not valid.",
        ErrorKind.Conflict => "The request conflicts with the current state.",
        ErrorKind.NotFound => "The resource was not found.",
        ErrorKind.Forbidden => "The operation is not allowed.",
        ErrorKind.PreconditionFailed => "The resource has changed.",
        ErrorKind.PreconditionRequired => "A precondition is required.",
        ErrorKind.Unauthorized => "Authentication is required.",
        _ => "An error occurred.",
    };
}

public static class ResultHttpExtensions
{
    public static IResult ToHttp<T>(this Result<T> result, Func<T, IResult> onSuccess) =>
        result.IsSuccess ? onSuccess(result.Value) : Problems.For(result.Error);
}

/// <summary>
/// The ProblemDetails responses every endpoint can give, declared for the OpenAPI documents (INT-001): 401, 403, 429
/// and 500 always; 400 when it takes input; 404 when its route names a resource; 409 and 422 when it changes something.
/// A status the endpoint declares itself is left as it is.
/// </summary>
public static class StandardProblems
{
    private static readonly string[] ContentTypes = ["application/problem+json"];

    public static void Declare(EndpointBuilder endpoint)
    {
        var declared = endpoint.Metadata.OfType<IProducesResponseTypeMetadata>().Select(m => m.StatusCode).ToHashSet();
        var methods = endpoint.Metadata.OfType<IHttpMethodMetadata>().SelectMany(m => m.HttpMethods).ToList();
        var changes = methods.Any(m => m is not "GET" and not "HEAD");
        var routeValues = endpoint is RouteEndpointBuilder route && route.RoutePattern.Parameters.Any(p => p.Name != "version");
        var statuses = new List<int> { StatusCodes.Status401Unauthorized, StatusCodes.Status403Forbidden, StatusCodes.Status429TooManyRequests, StatusCodes.Status500InternalServerError };
        if (changes || routeValues)
        {
            statuses.Add(StatusCodes.Status400BadRequest);
        }

        if (routeValues)
        {
            statuses.Add(StatusCodes.Status404NotFound);
        }

        if (changes)
        {
            statuses.AddRange([StatusCodes.Status409Conflict, StatusCodes.Status422UnprocessableEntity]);
        }

        foreach (var status in statuses.Where(s => !declared.Contains(s)))
        {
            endpoint.Metadata.Add(new ProducesResponseTypeMetadata(status, typeof(ProblemDetails), ContentTypes));
        }
    }
}
