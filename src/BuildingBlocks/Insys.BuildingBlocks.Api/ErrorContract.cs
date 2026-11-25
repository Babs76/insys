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
using Insys.BuildingBlocks.Domain;
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
            "Whole seconds to wait before retrying (on a 403, sent only with IDENTITY_NOT_LINKED: the sign-in is still being set up).", new OpenApiSchema { Type = JsonSchemaType.Integer, Minimum = "1" });

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
                    if (status is "403" or "429" or "503")
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

        // The codes an operation can answer with for these statuses whatever it does (access, idempotency, state); its own
        // codes are in the error code catalogue (x-error-codes-source).
        foreach (var (id, codes) in KnownCodes)
        {
            if (document.Components.Responses.TryGetValue(id, out var shared) && shared is OpenApiResponse known)
            {
                known.Description = $"{known.Description?.TrimEnd('.')}. Codes any operation may return: {string.Join(", ", codes)}.";
            }
        }
    }

    private static readonly (string Id, string[] Codes)[] KnownCodes =
    [
        ("Problem403", [ErrorCodes.Forbidden, ErrorCodes.TenantContextMissing, ErrorCodes.FunctionNotGranted, ErrorCodes.FunctionDisabled, ErrorCodes.FeatureNotEntitled,
            ErrorCodes.OutOfScope, ErrorCodes.ChannelNotEnabled, ErrorCodes.ChannelNotAllowed, ErrorCodes.ChannelApprovalRequired, ErrorCodes.IdentityNotLinked]),
        ("Problem409", [ErrorCodes.TenantNotActive, ErrorCodes.IdempotencyInProgress, ErrorCodes.IdempotencyResponseNotKept, ErrorCodes.InvalidStateTransition]),
        ("Problem422", [ErrorCodes.ValidationFailed, ErrorCodes.IdempotencyKeyRequired, ErrorCodes.IdempotencyKeyReused]),
    ];

    private static OpenApiSchema Enum(string description, params string[] values) => Enum(description, false, values);

    private static OpenApiSchema Enum(string description, bool nullable, params string[] values) => new()
    {
        Type = nullable ? JsonSchemaType.String | JsonSchemaType.Null : JsonSchemaType.String,
        Enum = [.. values.Select(v => (JsonNode)JsonValue.Create(v))],
        Description = description,
    };

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
            Description = "Validation failures (422): field name, as the request body spells it (camel case, with a path such as lines[0].amount for a nested field), to its messages.",
            AdditionalProperties = new OpenApiSchema { Type = JsonSchemaType.Array, Items = new OpenApiSchema { Type = JsonSchemaType.String } },
        };
        schema.Properties["candidates"] = new OpenApiSchema
        {
            Type = JsonSchemaType.Array | JsonSchemaType.Null,
            Description = "Only with PARTY_POSSIBLE_DUPLICATE (409, OQ-127): the existing parties the person registered matches (the same names, ignoring case, "
                + "and birth date). Use one, or register again with confirmNew and a reason.",
            Items = new OpenApiSchema
            {
                Type = JsonSchemaType.Object,
                Required = new HashSet<string> { "partyId", "partyNumber", "displayName" },
                Properties = new Dictionary<string, IOpenApiSchema>
                {
                    ["partyId"] = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" },
                    ["partyNumber"] = new OpenApiSchema { Type = JsonSchemaType.String },
                    ["displayName"] = new OpenApiSchema { Type = JsonSchemaType.String },
                    ["identifierLastFour"] = new OpenApiSchema
                    {
                        Type = JsonSchemaType.String | JsonSchemaType.Null,
                        Description = "The last four characters of one of its identity documents, to tell people apart; null when it has none.",
                    },
                },
            },
        };
        schema.Properties["blocks"] = new OpenApiSchema
        {
            Type = JsonSchemaType.Array | JsonSchemaType.Null,
            Description = "Only with PARTY_ANONYMISATION_BLOCKED (409, D-34): each thing that still keeps the party's data. Anonymise again once none is left.",
            Items = new OpenApiSchema
            {
                Type = JsonSchemaType.Object,
                Required = new HashSet<string> { "reason", "recordType", "recordId" },
                Properties = new Dictionary<string, IOpenApiSchema>
                {
                    ["reason"] = Enum("Why it blocks. OPEN: the record has not reached a final status. IN_RETENTION: it reached one, and the tenant's retention "
                        + "period from that business date runs until `until`. LEGAL_HOLD: a legal hold is in force on the record or one of its documents. "
                        + "NO_RETENTION_POLICY: the tenant has no retention period in force for the record's kind, so it is kept (fail closed).",
                        "OPEN", "IN_RETENTION", "LEGAL_HOLD", "NO_RETENTION_POLICY"),
                    ["recordType"] = Enum("The record. Its retention kind: LIFE_POLICY, GENERAL_POLICY and HEALTH_MEMBERSHIP are POLICY; LIFE_CLAIM, GENERAL_CLAIM "
                        + "and HEALTH_CLAIM are CLAIM; PAYMENT is PAYMENT. AUDIT_EVIDENCE is the audit events about the party. PARTY is the party itself (a legal "
                        + "hold on one of its own documents).",
                        "LIFE_POLICY", "LIFE_CLAIM", "GENERAL_POLICY", "GENERAL_CLAIM", "HEALTH_MEMBERSHIP", "HEALTH_CLAIM", "PAYMENT", "AUDIT_EVIDENCE", "PARTY"),
                    ["recordId"] = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid", Description = "The record; for AUDIT_EVIDENCE and PARTY, the party." },
                    ["retention"] = Enum("The retention period that applies: POLICY, CLAIM or PAYMENT (the tenant's record retention policies, "
                        + "/party/static/record-retention-policies; working default 7 years), or AUDIT (the longest audit retention class). Null for LEGAL_HOLD.",
                        nullable: true, "POLICY", "CLAIM", "PAYMENT", "CASE", "AUDIT"),
                    ["until"] = new OpenApiSchema
                    {
                        Type = JsonSchemaType.String | JsonSchemaType.Null,
                        Format = "date",
                        Description = "For IN_RETENTION: the first business date on which the record no longer blocks. Null for every other reason.",
                    },
                    ["holdId"] = new OpenApiSchema
                    {
                        Type = JsonSchemaType.String | JsonSchemaType.Null,
                        Format = "uuid",
                        Description = "For LEGAL_HOLD: the hold (null for a document flagged as held without a hold record). Null for every other reason.",
                    },
                    ["documentId"] = new OpenApiSchema
                    {
                        Type = JsonSchemaType.String | JsonSchemaType.Null,
                        Format = "uuid",
                        Description = "For LEGAL_HOLD on a document: the held document. Null for every other reason.",
                    },
                },
            },
        };
        // DE-039: DISCLOSURE_REQUIRED names what the console needs to open each notice already filled in.
        const string Disclosure = "Only with DISCLOSURE_REQUIRED (409, F060, DE-039): ";
        schema.Properties["displayPoint"] = Enum(Disclosure + "the display point whose disclosures aren't acknowledged.", nullable: true, "QUOTE", "ISSUE", "RENEWAL");
        schema.Properties["noticeTypes"] = new OpenApiSchema
        {
            Type = JsonSchemaType.Array | JsonSchemaType.Null,
            Description = Disclosure + "the notice types outstanding, by code. The same as the notice types in `notices`.",
            Items = new OpenApiSchema { Type = JsonSchemaType.String },
        };
        schema.Properties["notices"] = new OpenApiSchema
        {
            Type = JsonSchemaType.Array | JsonSchemaType.Null,
            Description = Disclosure + "each notice outstanding, by notice type then jurisdiction. Open its text with GET /integration/disclosures (staff) or /portal/disclosures "
                + "(the member) with `displayPoint` and these values, and acknowledge it with the same values plus `partyId`, `businessRefType` and `businessRefId`. "
                + "The acknowledgement answers with what is still outstanding, empty when the step can be tried again.",
            Items = new OpenApiSchema
            {
                Type = JsonSchemaType.Object,
                Required = new HashSet<string> { "noticeType", "jurisdiction", "language", "textVersion" },
                Properties = new Dictionary<string, IOpenApiSchema>
                {
                    ["noticeType"] = new OpenApiSchema { Type = JsonSchemaType.String },
                    ["jurisdiction"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "The party's country (address, else nationality), else the tenant's." },
                    ["language"] = new OpenApiSchema
                    {
                        Type = JsonSchemaType.String,
                        Description = "The party's preferred language if a text is in force in it, else the tenant's default language if one is, else the first language the notice exists in by code.",
                    },
                    ["textVersion"] = new OpenApiSchema { Type = JsonSchemaType.Integer, Description = "The version of that text in force today, the one an acknowledgement records." },
                },
            },
        };
        schema.Properties["partyId"] = new OpenApiSchema
        {
            Type = JsonSchemaType.String | JsonSchemaType.Null,
            Format = "uuid",
            Description = Disclosure + "the party who acknowledges: the proposer, policyholder or principal member.",
        };
        schema.Properties["businessRefType"] = new OpenApiSchema
        {
            Type = JsonSchemaType.String | JsonSchemaType.Null,
            Description = Disclosure + "the kind of record the step acts on (GENERAL_QUOTE, GENERAL_RENEWAL, LIFE_PROPOSAL); null for a Health enrolment, which has no record yet.",
        };
        schema.Properties["businessRefId"] = new OpenApiSchema
        {
            Type = JsonSchemaType.String | JsonSchemaType.Null,
            Format = "uuid",
            Description = Disclosure + "the record the step acts on; null for a Health enrolment.",
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
