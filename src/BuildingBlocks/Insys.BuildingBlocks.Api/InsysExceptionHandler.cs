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
        if (code == ErrorCodes.DatabaseBusy)
        {
            httpContext.Response.Headers.RetryAfter = DatabaseBusyRetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var context = new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails =
            {
                Status = status,
                Title = message,
                Extensions = { ["code"] = code },
            },
        };

        // BR-073: a missing or too long value a validator did not cover is a field error, as a validator's would be.
        if (exception is GuardException guard)
        {
            context.ProblemDetails.Extensions["errors"] = new Dictionary<string, string[]> { [guard.Field] = [guard.Problem] };
        }

        return await problemDetails.TryWriteAsync(context);
    }

    /// <summary>How long a client should wait after DATABASE_BUSY.</summary>
    public const int DatabaseBusyRetryAfterSeconds = 2;

    public static (int Status, string Code, string Message) Classify(Exception exception)
    {
        if (IsDatabaseBusy(exception))
        {
            return (503, ErrorCodes.DatabaseBusy, "The database is busy. Retry shortly.");
        }

        var postgres = exception as PostgresException ?? exception.InnerException as PostgresException;
        return exception switch
        {
            TenantContextMissingException => (403, ErrorCodes.TenantContextMissing, "No tenant is established for the caller."),
            TenantMismatchException => (403, ErrorCodes.Forbidden, "The operation is not allowed for this tenant."),
            OutOfScopeException => (403, ErrorCodes.OutOfScope, "The record is outside your product or organisation scope for this action."),
            GuardException => (422, ErrorCodes.ValidationFailed, "The request is not valid."),
            ChannelRefusedException refused => (403, refused.Code, refused.Message),
            DbUpdateConcurrencyException => (409, ErrorCodes.ConcurrencyConflict, "The record was changed by another request."),
            BadHttpRequestException bad => (bad.StatusCode, ErrorCodes.ValidationFailed, "The request could not be read."),
            _ when postgres is not null => ClassifyPostgres(postgres),
            _ => (500, ErrorCodes.InternalError, "An unexpected error occurred."),
        };
    }

    /// <summary>
    /// GAP-039: no pooled connection was free within the pool's wait (Npgsql gives up with a timeout), or PostgreSQL refused a
    /// new connection: too many connections (53300), a resource limit (53400), or the server not accepting them (57P03).
    /// Anywhere in the exception chain, since EF wraps them.
    /// </summary>
    public static bool IsDatabaseBusy(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is PostgresException { SqlState: PostgresErrorCodes.TooManyConnections or PostgresErrorCodes.ConfigurationLimitExceeded
                or PostgresErrorCodes.CannotConnectNow })
            {
                return true;
            }

            if (e is NpgsqlException { InnerException: TimeoutException } && e.Message.Contains("pool", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
