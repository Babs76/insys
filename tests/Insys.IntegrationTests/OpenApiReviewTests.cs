/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : IntegrationTests
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
using Insys.Testing;

namespace Insys.IntegrationTests;

/// <summary>
/// The Phase 7 OpenAPI review, kept as tests over the committed documents (INT-001, NFR-008, NFR-009): every operation is
/// named, summarised, secured and permissioned; documents its success response with a schema; documents its errors as
/// ProblemDetails; and declares the Idempotency-Key and If-Match headers it requires.
/// </summary>
public sealed class OpenApiReviewTests
{
    private static readonly string[] Documents = ["core", "static-data", "platform-operator"];
    private static readonly string[] Methods = ["get", "post", "put", "patch", "delete"];

    private static IEnumerable<(string Document, string Path, string Method, JsonObject Operation)> Operations() =>
        Documents.SelectMany(document =>
        {
            var root = JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryPaths.Root, "docs", "api", $"openapi-{document}.json")))!.AsObject();
            return root["paths"]!.AsObject().SelectMany(path => path.Value!.AsObject()
                .Where(m => Methods.Contains(m.Key))
                .Select(m => (document, path.Key, m.Key, Resolved(root, m.Value!.AsObject()))));
        });

    /// <summary>The operation with each shared response (GAP-005: "$ref": "#/components/responses/Problem404") in place.</summary>
    private static JsonObject Resolved(JsonObject root, JsonObject operation)
    {
        var copy = operation.DeepClone().AsObject();
        foreach (var (status, response) in (copy["responses"]?.AsObject() ?? []).ToList())
        {
            if (response?["$ref"]?.GetValue<string>() is { } reference && reference.StartsWith("#/components/responses/", StringComparison.Ordinal))
            {
                copy["responses"]![status] = root["components"]!["responses"]![reference["#/components/responses/".Length..]]!.DeepClone();
            }
        }

        return copy;
    }

    private static List<string> Violations(Func<(string Document, string Path, string Method, JsonObject Operation), bool> broken) =>
        Operations().Where(broken).Select(o => $"{o.Document}: {o.Method.ToUpperInvariant()} {o.Path}").ToList();

    private static JsonObject Responses(JsonObject operation) => operation["responses"]?.AsObject() ?? [];

    private static bool HasHeader(JsonObject operation, string name) =>
        operation["parameters"]?.AsArray().Any(p => p?["in"]?.GetValue<string>() == "header" && p["name"]?.GetValue<string>() == name) == true;

    [Fact]
    public void EveryOperation_IsNamedUniquely_Summarised_Tagged_AndPermissioned_INT001()
    {
        Violations(o => o.Operation["operationId"] is null || o.Operation["summary"] is null || o.Operation["tags"] is null || o.Operation["x-permission"] is null)
            .ShouldBeEmpty();
        Operations().GroupBy(o => o.Operation["operationId"]!.GetValue<string>()).Where(g => g.Count() > 1).Select(g => g.Key).ShouldBeEmpty();
    }

    [Fact]
    public void EverySuccess_IsDocumented_WithASchema_UnlessItHasNoBody()
    {
        Violations(o =>
        {
            var successes = Responses(o.Operation).Where(r => r.Key.StartsWith('2')).ToList();
            return successes.Count == 0 || successes.Any(r => r.Key is not "204" and not "202" && r.Value?["content"] is null);
        }).ShouldBeEmpty();
    }

    [Fact]
    public void EveryOperation_DocumentsItsErrors_AsProblemDetails()
    {
        Violations(o =>
        {
            var responses = Responses(o.Operation);
            string[] required = ["401", "403", "429", "500", .. o.Path.Contains('{', StringComparison.Ordinal) && o.Path != "/api/v1" ? new[] { "404" } : [],
                .. o.Method != "get" ? new[] { "409", "422" } : []];
            return required.Any(code => responses[code]?["content"]?["application/problem+json"] is null);
        }).ShouldBeEmpty();
    }

    [Fact]
    public void RequiredHeaders_AreDeclared_WithTheirResponses()
    {
        // An operation that answers 428 reads If-Match; one that is idempotent declares Idempotency-Key.
        Violations(o => Responses(o.Operation)["428"] is not null != HasHeader(o.Operation, "If-Match")).ShouldBeEmpty();
        Violations(o => HasHeader(o.Operation, "If-Match") && Responses(o.Operation)["412"] is null).ShouldBeEmpty();
        Operations().Count(o => HasHeader(o.Operation, "Idempotency-Key")).ShouldBeGreaterThan(100);
    }
}
