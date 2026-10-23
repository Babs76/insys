/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Testing
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
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Insys.Testing;

/// <summary>
/// Checks a real response against the schema its committed OpenAPI document gives it (GAP-031): the same properties,
/// no more and no fewer (the API writes nulls, so every property is present), with values of the documented types and
/// enums. A response documented with another module's shape fails here.
/// </summary>
public static class DocumentedSchema
{
    private static readonly ConcurrentDictionary<string, JsonObject> Documents = new(StringComparer.Ordinal);

    /// <summary>The problems found, by JSON path; empty when the value matches.</summary>
    public static IReadOnlyList<string> Problems(JsonElement value, string document, string schemaId)
    {
        var root = Load(document);
        var schema = root["components"]?["schemas"]?[schemaId]
            ?? throw new InvalidOperationException($"openapi-{document}.json has no schema {schemaId}.");
        var problems = new List<string>();
        Check(root, schema, value, "$", problems);
        return problems;
    }

    /// <summary>The problems found in an operation's 2xx response, checked against the schema its document gives it.</summary>
    public static IReadOnlyList<string> ResponseProblems(JsonElement value, string document, string operationId)
    {
        var root = Load(document);
        var operation = root["paths"]!.AsObject().SelectMany(p => p.Value!.AsObject().Select(m => m.Value))
            .SingleOrDefault(o => o?["operationId"]?.GetValue<string>() == operationId)
            ?? throw new InvalidOperationException($"openapi-{document}.json has no operation {operationId}.");
        var success = operation["responses"]!.AsObject().First(r => r.Key.StartsWith('2')).Value!;
        var problems = new List<string>();
        Check(root, success["content"]!["application/json"]!["schema"]!, value, "$", problems);
        return problems;
    }

    private static JsonObject Load(string document) => Documents.GetOrAdd(document, d =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryPaths.Root, "docs", "api", $"openapi-{d}.json")))!.AsObject());

    private static void Check(JsonObject root, JsonNode schema, JsonElement value, string path, List<string> problems)
    {
        if (schema["$ref"]?.GetValue<string>() is { } reference)
        {
            Check(root, root["components"]!["schemas"]![reference["#/components/schemas/".Length..]]!, value, path, problems);
            return;
        }

        if ((schema["oneOf"] ?? schema["anyOf"]) is { } alternatives)
        {
            var any = alternatives.AsArray().Any(a =>
            {
                var attempt = new List<string>();
                Check(root, a!, value, path, attempt);
                return attempt.Count == 0;
            });
            if (!any)
            {
                problems.Add($"{path}: matches none of the documented alternatives");
            }

            return;
        }

        var types = schema["type"] switch
        {
            JsonArray array => array.Select(t => t!.GetValue<string>()).ToHashSet(StringComparer.Ordinal),
            JsonValue single => [single.GetValue<string>()],
            _ => null,
        };
        if (value.ValueKind == JsonValueKind.Null)
        {
            if (types is not null && !types.Contains("null"))
            {
                problems.Add($"{path}: null, but the schema does not allow null");
            }

            return;
        }

        if (schema["enum"] is JsonArray values && value.ValueKind == JsonValueKind.String
            && !values.Any(v => v is not null && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>() == value.GetString()))
        {
            problems.Add($"{path}: '{value.GetString()}' is not one of the documented values");
        }

        if (types is null)
        {
            return; // Free-form (a JsonElement field).
        }

        var kind = value.ValueKind switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.Number => value.TryGetInt64(out _) ? "integer" : "number",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Array => "array",
            JsonValueKind.Object => "object",
            _ => "unknown",
        };
        if (!types.Contains(kind) && !(kind == "integer" && types.Contains("number")))
        {
            problems.Add($"{path}: {kind}, documented as {string.Join("|", types)}");
            return;
        }

        if (kind == "array" && schema["items"] is { } items)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                Check(root, items, item, $"{path}[{index++}]", problems);
            }
        }

        if (kind == "object")
        {
            var properties = schema["properties"]?.AsObject();
            if (properties is null)
            {
                if (schema["additionalProperties"] is JsonObject additional)
                {
                    foreach (var property in value.EnumerateObject())
                    {
                        Check(root, additional, property.Value, $"{path}.{property.Name}", problems);
                    }
                }

                return;
            }

            var actual = value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
            foreach (var missing in properties.Select(p => p.Key).Except(actual.Keys))
            {
                problems.Add($"{path}.{missing}: documented but not returned");
            }

            foreach (var (name, child) in actual)
            {
                if (properties[name] is { } childSchema)
                {
                    Check(root, childSchema, child, $"{path}.{name}", problems);
                }
                else
                {
                    problems.Add($"{path}.{name}: returned but not documented");
                }
            }
        }
    }
}
