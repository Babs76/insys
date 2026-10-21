using Insys.BuildingBlocks.Domain;
using Microsoft.AspNetCore.Http;

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
