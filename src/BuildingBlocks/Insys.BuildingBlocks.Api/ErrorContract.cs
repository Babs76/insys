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
using System.Text.Json.Nodes;
using Microsoft.OpenApi;

namespace Insys.BuildingBlocks.Api;

/// <summary>
/// Declares the error contract in each OpenAPI document (GAP-005): the ProblemDetails extensions every error carries,
/// and their headers: X-Correlation-Id (GAP-007), Content-Language, and Retry-After on 429 and 503 (GAP-006).
/// </summary>
public static class ErrorContract
{
    public const string CorrelationHeader = "X-Correlation-Id";
    public const string ContentLanguageHeader = "Content-Language";
    public const string RetryAfterHeader = "Retry-After";

    /// <summary>The code for an error written by the framework rather than an endpoint (authentication, routing).</summary>
    public static string? CodeFor(int status) => status switch
    {
        401 => Domain.ErrorCodes.Unauthenticated,
        403 => Domain.ErrorCodes.Forbidden,
        404 => Domain.ErrorCodes.NotFound,
        412 => Domain.ErrorCodes.PreconditionFailed,
        428 => Domain.ErrorCodes.PreconditionRequired,
        429 => Domain.ErrorCodes.RateLimited,
        500 => Domain.ErrorCodes.InternalError,
        503 => Domain.ErrorCodes.NotReady,
        _ => null,
    };

    public static void Apply(OpenApiDocument document)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.Headers ??= new Dictionary<string, IOpenApiHeader>();
        document.Components.Headers[CorrelationHeader] = Header(
            "The request's correlation ID: the caller's X-Correlation-Id when it sent a valid one, else a new one. Errors carry it in correlationId too.",
            new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" });
        document.Components.Headers[ContentLanguageHeader] = Header(
            "The language of the error's title (docs/specs/addenda/localisation.md §4).", new OpenApiSchema { Type = JsonSchemaType.String });
        document.Components.Headers[RetryAfterHeader] = Header(
            "Whole seconds to wait before retrying.", new OpenApiSchema { Type = JsonSchemaType.Integer, Minimum = "1" });

        if (document.Components.Schemas?.TryGetValue("ProblemDetails", out var problem) == true && problem is OpenApiSchema schema)
        {
            Extend(schema);
        }

        // Every error response is the same ProblemDetails for its status, so each is one shared response with its headers,
        // referenced from the operations. Success responses carry X-Correlation-Id too; the document description says so.
        document.Components.Responses ??= new Dictionary<string, IOpenApiResponse>();
        foreach (var operation in (document.Paths ?? new OpenApiPaths()).Values.SelectMany(p => p.Operations?.Values.AsEnumerable() ?? []))
        {
            foreach (var (status, response) in (operation.Responses ?? new OpenApiResponses()).ToList())
            {
                if (status.Length != 3 || status[0] is not ('4' or '5') || response is not OpenApiResponse concrete
                    || concrete.Content?.Keys.SingleOrDefault() != "application/problem+json")
                {
                    continue;
                }

                var id = "Problem" + status;
                if (!document.Components.Responses.ContainsKey(id))
                {
                    concrete.Headers = new Dictionary<string, IOpenApiHeader>
                    {
                        [CorrelationHeader] = new OpenApiHeaderReference(CorrelationHeader, document),
                        [ContentLanguageHeader] = new OpenApiHeaderReference(ContentLanguageHeader, document),
                    };
                    if (status is "429" or "503")
                    {
                        concrete.Headers[RetryAfterHeader] = new OpenApiHeaderReference(RetryAfterHeader, document);
                    }

                    document.Components.Responses[id] = concrete;
                }
                else if (document.Components.Responses[id].Description != concrete.Description)
                {
                    continue;
                }

                operation.Responses![status] = new OpenApiResponseReference(id, document);
            }
        }
    }

    private static OpenApiHeader Header(string description, OpenApiSchema schema) => new() { Description = description, Schema = schema };

    private static void Extend(OpenApiSchema schema)
    {
        schema.Description = "RFC 9457 problem details. `code` is stable and never localised; `title` and `detail` follow the request's language.";
        schema.Properties ??= new Dictionary<string, IOpenApiSchema>();
        schema.Properties["code"] = new OpenApiSchema
        {
            Type = JsonSchemaType.String,
            Description = "The stable error code (the error code catalogue), for example VALIDATION_FAILED, NOT_FOUND, RATE_LIMITED or NOT_READY. "
                + "Present on every error except a malformed request (400, 405, 415) rejected before an endpoint runs.",
        };
        schema.Properties["errors"] = new OpenApiSchema
        {
            Type = JsonSchemaType.Object | JsonSchemaType.Null,
            Description = "Validation failures (422): field name to its messages.",
            AdditionalProperties = new OpenApiSchema { Type = JsonSchemaType.Array, Items = new OpenApiSchema { Type = JsonSchemaType.String } },
        };
        schema.Properties["correlationId"] = new OpenApiSchema
        {
            Type = JsonSchemaType.String,
            Format = "uuid",
            Description = "The same value as the X-Correlation-Id response header, on every error (GAP-007).",
        };
        schema.Properties["traceId"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "The server's trace identifier, for support." };
        schema.Properties["detailLanguage"] = new OpenApiSchema
        {
            Type = JsonSchemaType.String | JsonSchemaType.Null,
            Description = "Present when the detail stayed in English because the catalogue has no detail in the title's language; its value is then \"en\".",
        };
        schema.Required = new HashSet<string>(schema.Required ?? new HashSet<string>()) { "correlationId" };
        schema.Extensions ??= new Dictionary<string, IOpenApiExtension>();
        schema.Extensions["x-error-codes-source"] = new JsonNodeExtension(JsonValue.Create("GET /api/v1/integration/static/error-code-catalogue"));
    }
}
