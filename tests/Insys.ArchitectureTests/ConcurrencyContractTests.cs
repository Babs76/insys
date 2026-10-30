/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : ArchitectureTests
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
using System.Text.Json;
using System.Text.RegularExpressions;
using Insys.Testing;

namespace Insys.ArchitectureTests;

/// <summary>
/// BR-031, BR-057, BR-064, BR-071: in General, the claims framework, Billing, Distribution, Life and Health, every change
/// to an existing record takes If-Match and every create an Idempotency-Key, and the record a change is on has a read
/// whose response carries its rowVersion, which the API gives as the ETag. Held on the committed contract.
/// </summary>
public sealed partial class ConcurrencyContractTests
{
    /// <summary>
    /// Writes that change nothing: an eligibility check answers a question, and a search reads with its filters in the
    /// body so a member number stays out of the address (BR-069).
    /// </summary>
    private static readonly HashSet<string> ChangeNothing = ["HealthCheckEligibility", "HealthSearchMemberships", "HealthListEligibilityDecisions"];

    private static readonly JsonElement Paths =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryPaths.Root, "docs", "api", "openapi-core.json"))).RootElement.GetProperty("paths");

    [Fact]
    public void EveryChange_TakesIfMatch_AndEveryCreate_AnIdempotencyKey_BR031()
    {
        var unprotected = Writes().Where(w => !ChangeNothing.Contains(w.OperationId) && !w.Headers.Contains("If-Match") && !w.Headers.Contains("Idempotency-Key"))
            .Select(w => $"{w.Method} {w.Path} ({w.OperationId})").ToList();

        unprotected.ShouldBeEmpty();
        Writes().Count(w => w.Headers.Contains("If-Match")).ShouldBeGreaterThan(80);
    }

    [Fact]
    public void TheRecordAChangeIsOn_IsReadWithItsRowVersion_TheETag_BR031()
    {
        var missing = new List<string>();
        foreach (var write in Writes().Where(w => w.Headers.Contains("If-Match")))
        {
            var record = RecordPath().Match(write.Path).Value;
            if (!Paths.TryGetProperty(record, out var item) || !item.TryGetProperty("get", out var get)
                || !get.GetProperty("responses").TryGetProperty("200", out var ok) || !HasRowVersion(ok))
            {
                missing.Add($"{write.OperationId}: {record}");
            }
        }

        missing.ShouldBeEmpty();
    }

    /// <summary>
    /// BR-116: an operation taking If-Match can be called only by someone who can read the version it asks for. Its caller's own
    /// read, on its path or a parent within the same area (a portal's stays in the portal), gives an ETag or a rowVersion.
    /// </summary>
    [Theory]
    [InlineData("core")]
    [InlineData("platform-operator")]
    [InlineData("static-data")]
    public void EveryIfMatchOperation_HasAReadByTheSameCaller_GivingItsVersion_BR116(string document)
    {
        var root = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryPaths.Root, "docs", "api", $"openapi-{document}.json"))).RootElement;
        var paths = root.GetProperty("paths");
        var schemas = root.GetProperty("components").GetProperty("schemas");
        var missing = new List<string>();
        foreach (var path in paths.EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject().Where(o => o.Name is "post" or "put" or "patch" or "delete" && TakesIfMatch(o.Value)))
            {
                var candidates = Ancestors(path.Name).Where(p => paths.TryGetProperty(p, out _)).Select(p => paths.GetProperty(p))
                    .Where(p => p.TryGetProperty("get", out _)).Select(p => p.GetProperty("get"));
                if (!candidates.Any(get => GivesVersion(get, schemas)))
                {
                    missing.Add($"{operation.Value.GetProperty("operationId").GetString()}: {path.Name}");
                }
            }
        }

        missing.ShouldBeEmpty();
    }

    private static bool TakesIfMatch(JsonElement operation) =>
        operation.TryGetProperty("parameters", out var parameters) && parameters.EnumerateArray().Any(p => p.GetProperty("name").GetString() == "If-Match");

    private static IEnumerable<string> Ancestors(string path)
    {
        for (var at = path; at.Count(c => c == '/') >= 3; at = at[..at.LastIndexOf('/')])
        {
            yield return at;
        }
    }

    private static bool GivesVersion(JsonElement get, JsonElement schemas)
    {
        if (!get.GetProperty("responses").TryGetProperty("200", out var ok))
        {
            return false;
        }

        if (ok.TryGetProperty("headers", out var headers) && headers.TryGetProperty("ETag", out _))
        {
            return true;
        }

        return ok.TryGetProperty("content", out var content) && content.TryGetProperty("application/json", out var json) && NamesRowVersion(json.GetProperty("schema"), schemas, 0);
    }

    /// <summary>The schema, or a page's or a list's items, has a rowVersion.</summary>
    private static bool NamesRowVersion(JsonElement schema, JsonElement schemas, int depth)
    {
        if (depth > 3)
        {
            return false;
        }

        if (schema.TryGetProperty("$ref", out var reference))
        {
            return NamesRowVersion(schemas.GetProperty(reference.GetString()!.Split('/')[^1]), schemas, depth + 1);
        }

        if (schema.TryGetProperty("items", out var items) && NamesRowVersion(items, schemas, depth + 1))
        {
            return true;
        }

        return schema.TryGetProperty("properties", out var properties)
            && (properties.TryGetProperty("rowVersion", out _) || (properties.TryGetProperty("items", out var page) && NamesRowVersion(page, schemas, depth + 1)));
    }

    private static IEnumerable<(string Method, string Path, string OperationId, HashSet<string> Headers)> Writes() =>
        Paths.EnumerateObject()
            .Where(p => ModulePath().IsMatch(p.Name))
            .SelectMany(p => p.Value.EnumerateObject().Where(o => o.Name is "post" or "put" or "patch" or "delete").Select(o => (
                o.Name.ToUpperInvariant(), p.Name, o.Value.GetProperty("operationId").GetString()!,
                o.Value.TryGetProperty("parameters", out var parameters)
                    ? parameters.EnumerateArray().Where(x => x.GetProperty("in").GetString() == "header" && x.TryGetProperty("required", out var r) && r.GetBoolean())
                        .Select(x => x.GetProperty("name").GetString()!).ToHashSet()
                    : [])));

    private static bool HasRowVersion(JsonElement response)
    {
        if (!response.TryGetProperty("content", out var content) || !content.TryGetProperty("application/json", out var json))
        {
            return false;
        }

        var schema = json.GetProperty("schema");
        if (schema.TryGetProperty("$ref", out var reference))
        {
            var name = reference.GetString()!.Split('/')[^1];
            schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryPaths.Root, "docs", "api", "openapi-core.json"))).RootElement
                .GetProperty("components").GetProperty("schemas").GetProperty(name);
        }

        return schema.TryGetProperty("properties", out var properties) && properties.TryGetProperty("rowVersion", out _);
    }

    [GeneratedRegex(@"^/api/v1/(general|claims|billing|distribution|life|health)/")]
    private static partial Regex ModulePath();

    [GeneratedRegex(@"^/api/v1/[^/]+/[^/]+/\{[^}]+\}")]
    private static partial Regex RecordPath();
}
