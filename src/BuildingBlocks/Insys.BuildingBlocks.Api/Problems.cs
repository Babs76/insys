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
using Insys.BuildingBlocks.Application.Localisation;
using Insys.BuildingBlocks.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

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
/// The ProblemDetails responses every endpoint can give, declared for the OpenAPI documents (INT-001): 401, 403, 429,
/// 500 and 503 (NOT_READY, while a new instance warms up) always; 400 when it takes input; 404 when its route names a resource; 409 and 422 when it changes something.
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
        var statuses = new List<int>
        {
            StatusCodes.Status401Unauthorized, StatusCodes.Status403Forbidden, StatusCodes.Status429TooManyRequests, StatusCodes.Status500InternalServerError,
            StatusCodes.Status503ServiceUnavailable,
        };
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

/// <summary>
/// Localises a ProblemDetails from the message catalogue (docs/specs/addenda/localisation.md §4, OQ-068): the title by
/// the error code's message_key and the request's fallback chain; the detail only where the catalogue has one in the
/// same language, else the server's English detail is kept and marked detailLanguage "en". The code never changes.
/// Content-Language is the title's language; without catalogue text the built-in English stays.
/// </summary>
public static class ProblemLocalisation
{
    public static void Apply(HttpContext http, ProblemDetails problem)
    {
        if (http.RequestServices.GetService<RequestLanguage>() is not { } request
            || !problem.Extensions.TryGetValue("code", out var value) || value is not string code)
        {
            return;
        }

        string? language = null;
        foreach (var (each, texts) in request.Messages)
        {
            if (texts.Titles.TryGetValue(code, out var title))
            {
                problem.Title = title;
                language = each;
                if (texts.Details.TryGetValue(code, out var detail))
                {
                    problem.Detail = detail;
                }
                else if (problem.Detail is not null && each != TenantLanguages.English)
                {
                    problem.Extensions["detailLanguage"] = TenantLanguages.English;
                }

                break;
            }
        }

        http.Response.Headers.ContentLanguage = language ?? TenantLanguages.English;
    }
}

/// <summary>
/// The correlation ID of an error response (GAP-007), in the X-Correlation-Id header and the ProblemDetails body alike:
/// the request's execution scope's ID, or, for an error written before the scope exists (the warm-up gate, say), the
/// caller's X-Correlation-Id, else a new one.
/// </summary>
public static class ErrorCorrelation
{
    public static Guid Ensure(HttpContext http)
    {
        var scope = http.RequestServices.GetService<Insys.BuildingBlocks.Application.Context.ExecutionScope>();
        Guid id;
        if (scope is { IsInitialised: true })
        {
            id = scope.CorrelationId;
        }
        else if (!Guid.TryParse(http.Response.Headers[ExecutionScopeMiddleware.CorrelationHeader], out id)
                 && !Guid.TryParse(http.Request.Headers[ExecutionScopeMiddleware.CorrelationHeader], out id))
        {
            id = Guid.CreateVersion7();
        }

        if (!http.Response.HasStarted)
        {
            http.Response.Headers[ExecutionScopeMiddleware.CorrelationHeader] = id.ToString();
        }

        return id;
    }
}
