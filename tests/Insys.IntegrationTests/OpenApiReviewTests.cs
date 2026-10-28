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

    private static bool HasHeader(JsonObject operation, string name, bool requiredOnly = false) =>
        operation["parameters"]?.AsArray().Any(p => p?["in"]?.GetValue<string>() == "header" && p["name"]?.GetValue<string>() == name
                                                    && (!requiredOnly || p["required"]?.GetValue<bool>() == true)) == true;

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
        // A required If-Match answers 428 when missing; an optional one (an idempotent replacement) only 412 when stale.
        Violations(o => Responses(o.Operation)["428"] is not null != HasHeader(o.Operation, "If-Match", requiredOnly: true)).ShouldBeEmpty();
        Violations(o => HasHeader(o.Operation, "If-Match") && Responses(o.Operation)["412"] is null).ShouldBeEmpty();
        Operations().Count(o => HasHeader(o.Operation, "Idempotency-Key")).ShouldBeGreaterThan(100);
    }

    /// <summary>
    /// GAP-031: the response check finds a record documented with another module's shape. General's proposal (seven
    /// fields, with its rowVersion since BR-031 and its policyId since BR-035) passes against GeneralProposalView and fails against Life's
    /// LifeProposalView, as it was documented before.
    /// </summary>
    [Fact]
    public void ResponseCheck_FindsARecordDocumentedWithAnotherModulesShape_GAP031()
    {
        using var proposal = System.Text.Json.JsonDocument.Parse("""
            { "id": "0199f0a0-0000-7000-8000-000000000001", "quoteId": "0199f0a0-0000-7000-8000-000000000002", "status": "Submitted",
              "submittedAt": "2026-10-23T10:00:00+00:00", "declarations": { "claims_last_3_years": 0 }, "rowVersion": 1, "policyId": null }
            """);

        Insys.Testing.DocumentedSchema.Problems(proposal.RootElement, "core", "GeneralProposalView").ShouldBeEmpty();
        Insys.Testing.DocumentedSchema.Problems(proposal.RootElement, "core", "LifeProposalView").ShouldNotBeEmpty();
    }

    /// <summary>
    /// GAP-052: every permission the code enforces is named in the contract. An endpoint's own is its x-permission; one a
    /// handler checks (execution.HasPermission) is declared on some operation, and one an approval request names for its
    /// checker on Workflow's approve and reject operations, as x-additional-permissions.
    /// </summary>
    [Fact]
    public void EveryEnforcedPermission_IsDeclaredInTheContract_GAP052()
    {
        var constants = PermissionConstants();
        var declared = new HashSet<string>(StringComparer.Ordinal);
        var onApprovals = new HashSet<string>(StringComparer.Ordinal);
        foreach (var o in Operations())
        {
            var additional = (o.Operation["x-additional-permissions"]?.AsArray() ?? []).Select(a => a!["permission"]!.GetValue<string>()).ToList();
            declared.UnionWith(additional);
            if (o.Operation["x-permission"]?.GetValue<string>() is { } own)
            {
                declared.Add(own);
            }

            if (o.Operation["operationId"]?.GetValue<string>() is "WorkflowApprove" or "WorkflowReject")
            {
                onApprovals.UnionWith(additional);
            }
        }

        var undeclared = new List<string>();
        var sources = Directory.EnumerateFiles(Path.Combine(RepositoryPaths.Root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        foreach (var file in sources)
        {
            var text = File.ReadAllText(file);
            foreach (System.Text.RegularExpressions.Match check in System.Text.RegularExpressions.Regex.Matches(text, @"\.HasPermission\(([^()]+)\)"))
            {
                var argument = check.Groups[1].Value.Trim();
                if (argument is "requirement.Permission" or "permission" && Path.GetFileName(file) is "Security.cs" or "Approvals.cs")
                {
                    continue; // the endpoint's own permission, and the one an approval names (checked below)
                }

                var value = Resolve(constants, argument, file);
                if (!declared.Contains(value))
                {
                    undeclared.Add($"{Path.GetFileName(file)}: {value} is checked but declared on no operation");
                }
            }

            foreach (var request in ApprovalRequests(text))
            {
                foreach (System.Text.RegularExpressions.Match named in System.Text.RegularExpressions.Regex.Matches(request, @"[\w.]*Permissions\.\w+"))
                {
                    var value = Resolve(constants, named.Value, file);
                    if (!onApprovals.Contains(value))
                    {
                        undeclared.Add($"{Path.GetFileName(file)}: approvals name {value}, but WorkflowApprove and WorkflowReject do not declare it");
                    }
                }
            }
        }

        undeclared.ShouldBeEmpty();
        declared.ShouldContain("commission_payout.read");
    }

    private static string Resolve(Dictionary<string, string> constants, string expression, string file)
    {
        var key = string.Join('.', expression.Split('.').TakeLast(2));
        constants.TryGetValue(key, out var value).ShouldBeTrue($"{Path.GetFileName(file)}: {expression} is not a permission constant");
        return value!;
    }

    /// <summary>The arguments of each ApprovalRequested construction in a source file.</summary>
    private static IEnumerable<string> ApprovalRequests(string text)
    {
        for (var at = text.IndexOf("ApprovalRequested(", StringComparison.Ordinal); at >= 0; at = text.IndexOf("ApprovalRequested(", at + 1, StringComparison.Ordinal))
        {
            var start = at + "ApprovalRequested(".Length;
            var depth = 1;
            var end = start;
            while (end < text.Length && depth > 0)
            {
                depth += text[end] switch { '(' => 1, ')' => -1, _ => 0 };
                end++;
            }

            yield return text[start..(end - 1)];
        }
    }

    /// <summary>Every const string of a *Permissions class in the Insys assemblies, by "Class.Name".</summary>
    private static Dictionary<string, string> PermissionConstants() =>
        Directory.EnumerateFiles(AppContext.BaseDirectory, "Insys.*.dll")
            .Select(System.Reflection.Assembly.LoadFrom)
            .SelectMany(a => a.GetTypes())
            .Where(t => t.IsAbstract && t.IsSealed && t.Name.EndsWith("Permissions", StringComparison.Ordinal))
            .SelectMany(t => t.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Where(f => f.IsLiteral && f.FieldType == typeof(string))
                .Select(f => (Key: $"{t.Name}.{f.Name}", Value: (string)f.GetRawConstantValue()!)))
            .GroupBy(c => c.Key)
            .ToDictionary(g => g.Key, g => g.First().Value);
}
