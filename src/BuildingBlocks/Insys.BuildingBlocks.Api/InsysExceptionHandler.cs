using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Insys.BuildingBlocks.Api;

/// <summary>
/// Turns unhandled exceptions into ProblemDetails. Database rule violations (unique keys, lifecycle triggers,
/// row-level security) become 409; nothing from the exception message reaches the client or the log, because it
/// may contain data values.
/// </summary>
public sealed class InsysExceptionHandler(IProblemDetailsService problemDetails, ILogger<InsysExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, code, message) = Classify(exception);
        if (status >= 500)
        {
            logger.LogError("Unhandled {ExceptionType} on {Method} {Path}", exception.GetType().FullName, httpContext.Request.Method, httpContext.Request.Path);
        }
        else
        {
            logger.LogInformation("{ExceptionType} mapped to {Status} {Code}", exception.GetType().Name, status, code);
        }

        httpContext.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails =
            {
                Status = status,
                Title = message,
                Extensions = { ["code"] = code },
            },
        });
    }

    public static (int Status, string Code, string Message) Classify(Exception exception)
    {
        var postgres = exception as PostgresException ?? exception.InnerException as PostgresException;
        return exception switch
        {
            TenantContextMissingException => (403, ErrorCodes.TenantContextMissing, "No tenant is established for the caller."),
            TenantMismatchException => (403, ErrorCodes.Forbidden, "The operation is not allowed for this tenant."),
            DbUpdateConcurrencyException => (409, ErrorCodes.ConcurrencyConflict, "The record was changed by another request."),
            BadHttpRequestException bad => (bad.StatusCode, ErrorCodes.ValidationFailed, "The request could not be read."),
            _ when postgres is not null => ClassifyPostgres(postgres),
            _ => (500, ErrorCodes.InternalError, "An unexpected error occurred."),
        };
    }

    private static (int, string, string) ClassifyPostgres(PostgresException postgres) => postgres.SqlState switch
    {
        PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ExclusionViolation => (409, ErrorCodes.DuplicateRecord, "A record with the same key already exists."),
        PostgresErrorCodes.CheckViolation or PostgresErrorCodes.ForeignKeyViolation or PostgresErrorCodes.NotNullViolation or PostgresErrorCodes.RaiseException
            => (409, ErrorCodes.DatabaseRuleViolated, "The change breaks a rule enforced by the database."),
        PostgresErrorCodes.InsufficientPrivilege => (403, ErrorCodes.Forbidden, "The operation is not allowed."),
        _ => (500, ErrorCodes.InternalError, "An unexpected error occurred."),
    };
}
