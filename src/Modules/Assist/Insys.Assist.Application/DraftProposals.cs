/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Assist
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
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Insys.Assist.Domain;
using Insys.BuildingBlocks.Application;
using Insys.ProductFactory.Contracts;

namespace Insys.Assist.Application;

/// <summary>One field AI-1 may propose a value for (AIA-012), generated from the model, never written into a prompt by hand.</summary>
/// <param name="Path"><c>version.&lt;field&gt;</c> for the version's own fields, else the static data table's name.</param>
/// <param name="Allowed">The values allowed, when the field takes a code.</param>
/// <param name="Columns">A table's fields (StaticDataFields), whose rows the AI proposes.</param>
/// <param name="Required">Required by the model: Missing when no value passes (AIA-014).</param>
public sealed record DraftField(string Path, string Kind, IReadOnlyList<string>? Allowed, IReadOnlyList<string>? Columns, bool Required, bool InDraft);

public static class DraftFields
{
    public const string Name = "version.name";
    public const string Coverages = "version.coverages";
    public const string EffectiveFrom = "version.effectiveFrom";
    public const string CoverStartsOn = "version.coverStartsOn";
    public const string ReinstatementArrears = "version.reinstatementArrears";

    /// <summary>A rate table (AIA-015): copied cell for cell, and checked against the source table.</summary>
    public const string RateTables = "rating.cfg_rating_tables";

    // Tables that describe distribution, access or reinsurance rather than the product's terms, though they name a product.
    private static readonly HashSet<string> NotProductTerms = new(StringComparer.Ordinal) { "channel", "commission", "iam", "reinsurance" };

    /// <summary>
    /// The field list for the product (AIA-012, OQ-184 (a)):
    /// <list type="bullet">
    /// <item>the version's own fields, written to the Draft, with the codes its domain allows;</item>
    /// <item>every static data table of Product Factory, Rating, Rules or the product's own line that refers to a product version (generated from the
    /// catalogue's references), proposed only;</item>
    /// <item>and the rate tables those parameters name.</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<DraftField> For(ProductDraftTarget target)
    {
        var line = target.Domain.ToLowerInvariant();
        var fields = new List<DraftField>
        {
            new(Name, DraftItemKinds.Value, null, null, Required: true, InDraft: true),
            new(Coverages, DraftItemKinds.Value, target.CoverageCodes, null, Required: true, InDraft: true),
            new(EffectiveFrom, DraftItemKinds.Value, null, null, Required: false, InDraft: true),
            new(CoverStartsOn, DraftItemKinds.Value, ["Issue", "FirstPayment"], null, Required: false, InDraft: true),
            new(ReinstatementArrears, DraftItemKinds.Value, ["None", "Required"], null, Required: false, InDraft: true),
        };

        var tables = StaticDataReferences.Refers
            .Where(r => r.Value.Contains("product.cfg_product_definitions_and_versions") && r.Key != "product.cfg_product_definitions_and_versions")
            .Select(r => r.Key)
            .Where(t => t.Split('.')[0] is var schema && !NotProductTerms.Contains(schema) && (schema is "product" or "rating" or "rules" || schema == line))
            .Order(StringComparer.Ordinal);
        fields.AddRange(tables.Select(t => new DraftField(t, DraftItemKinds.Value, null, StaticDataFields.Of.GetValueOrDefault(t, []), Required: false, InDraft: false)));
        fields.Add(new DraftField(RateTables, DraftItemKinds.RateTable, null, StaticDataFields.Of.GetValueOrDefault(RateTables, []), Required: false, InDraft: false));
        return fields;
    }
}

/// <summary>A document as sent: its number in the run, and its pages and tables after the minimiser (AIA-051), which the citations must match.</summary>
public sealed record SentDocument(int Number, Guid DocumentId, string? FileName, ExtractedDocument Extracted);

/// <summary>What AI-1 sends: the field list and the documents' text, as data only (AIA-021).</summary>
public static class DraftPayload
{
    public static string Render(ProductDraftTarget target, IReadOnlyList<DraftField> fields, IReadOnlyList<SentDocument> documents)
    {
        var json = new JsonObject
        {
            ["product"] = new JsonObject { ["code"] = target.ProductCode, ["domain"] = target.Domain },
            ["fields"] = new JsonArray(fields.Select(f => (JsonNode)new JsonObject
            {
                ["field"] = f.Path, ["kind"] = f.Kind, ["required"] = f.Required,
                ["allowed"] = f.Allowed is null ? null : new JsonArray(f.Allowed.Select(a => (JsonNode)a).ToArray()),
                ["columns"] = f.Columns is null ? null : new JsonArray(f.Columns.Select(c => (JsonNode)c).ToArray()),
            }).ToArray()),
            ["documents"] = new JsonArray(documents.Select(d => (JsonNode)new JsonObject
            {
                ["document"] = d.Number,
                ["pages"] = new JsonArray(d.Extracted.Pages.Select(p => (JsonNode)new JsonObject { ["page"] = p.Number, ["text"] = p.Text }).ToArray()),
                ["tables"] = new JsonArray(d.Extracted.Tables.Select(t => (JsonNode)new JsonObject
                {
                    ["page"] = t.Page, ["table"] = t.Index,
                    ["rows"] = new JsonArray(t.Rows.Select(r => (JsonNode)new JsonArray(r.Select(c => (JsonNode)c).ToArray())).ToArray()),
                }).ToArray()),
                ["unreadablePages"] = new JsonArray(d.Extracted.UnreadablePages.Select(p => (JsonNode)p).ToArray()),
            }).ToArray()),
        };
        return json.ToJsonString();
    }
}

/// <summary>One item after INSYS's checks: kept (Dropped null) or dropped with the reason, and whether it goes into the Draft.</summary>
public sealed record CheckedItem(string Kind, string Field, string? Value, string Flag, Guid? DocumentId, int? Page, string? Quote, string? Dropped, bool InDraft);

/// <summary>
/// INSYS's deterministic checks on what AI-1's model returned (AIA-012 to AIA-016, AIA-021): nothing it says is trusted.
/// <list type="bullet">
/// <item>A field not in the field list is dropped (AIA-012), and so is a code not allowed.</item>
/// <item>A value without a citation, or whose quote isn't in the cited page's text as sent, is dropped (AIA-013).</item>
/// <item>Found needs the value in its quote; otherwise it is Interpreted, needing review (AIA-014). Missing is INSYS's to say, never the model's.</item>
/// <item>A rate table must equal a table extracted from its cited page, cell for cell (AIA-015).</item>
/// <item>A rule is kept only in plain words, never as an expression, and never reaches the Draft (AIA-016).</item>
/// </list>
/// The output must be one JSON object; anything else rejects the run (ASSIST_OUTPUT_REJECTED).
/// </summary>
public static partial class DraftProposalCheck
{
    public const string UnknownField = "UNKNOWN_FIELD";
    public const string NotAllowed = "NOT_ALLOWED";
    public const string NoCitation = "NO_CITATION";
    public const string CitationNotFound = "CITATION_NOT_FOUND";
    public const string TableMismatch = "TABLE_MISMATCH";
    public const string RuleExpression = "RULE_EXPRESSION";
    public const string Duplicate = "DUPLICATE";
    public const string Malformed = "MALFORMED";

    /// <summary>At most this many proposals are read; the rest are ignored, so an output can't flood the run.</summary>
    public const int MaxItems = 400;

    private const int MaxValueLength = 20_000;

    /// <summary>The checked items, Missing ones included, or null when the output isn't one JSON object.</summary>
    public static IReadOnlyList<CheckedItem>? Check(string output, IReadOnlyList<DraftField> fields, IReadOnlyList<SentDocument> documents)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(Unfence(output)) as JsonObject ?? throw new JsonException();
        }
        catch (JsonException)
        {
            return null;
        }

        var byPath = fields.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var items = new List<CheckedItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var read = 0;

        foreach (var node in Array(root, "values").Concat(Array(root, "rows")))
        {
            if (read++ >= MaxItems)
            {
                break;
            }

            items.Add(Value(node, byPath, documents, seen));
        }

        foreach (var node in Array(root, "rateTables"))
        {
            if (read++ >= MaxItems)
            {
                break;
            }

            items.Add(RateTable(node, documents));
        }

        foreach (var node in Array(root, "rules"))
        {
            if (read++ >= MaxItems)
            {
                break;
            }

            items.Add(Rule(node, documents));
        }

        foreach (var node in Array(root, "simulationCases"))
        {
            if (read++ >= MaxItems)
            {
                break;
            }

            items.Add(SimulationCase(node, documents));
        }

        // Missing (AIA-014): a required field with no value kept, whatever the model said about it.
        foreach (var field in fields.Where(f => f.Required && !items.Any(i => i.Field == f.Path && i.Dropped is null)))
        {
            items.Add(new CheckedItem(DraftItemKinds.Value, field.Path, null, DraftItemFlags.Missing, null, null, null, null, false));
        }

        return items;
    }

    private static CheckedItem Value(JsonNode? node, Dictionary<string, DraftField> fields, IReadOnlyList<SentDocument> documents, HashSet<string> seen)
    {
        var path = Text(node, "field") ?? "";
        var value = node?["value"];
        var (documentId, page, quote, citation) = Citation(node, documents);
        if (!fields.TryGetValue(path, out var field) || field.Kind != DraftItemKinds.Value)
        {
            return Dropped(DraftItemKinds.Value, Clip(path, 160), value, documentId, page, quote, UnknownField);
        }

        if (value is null || Serialise(value) is not { } json)
        {
            return Dropped(DraftItemKinds.Value, path, null, documentId, page, quote, Malformed);
        }

        if (!Allowed(field, value))
        {
            return Dropped(DraftItemKinds.Value, path, value, documentId, page, quote, field.Columns is null ? NotAllowed : UnknownField);
        }

        if (citation is not null)
        {
            return Dropped(DraftItemKinds.Value, path, value, documentId, page, quote, citation);
        }

        // One value for each of the version's own fields: the first kept, later ones dropped. A table's rows may be many.
        if (field.InDraft && !seen.Add(path))
        {
            return Dropped(DraftItemKinds.Value, path, value, documentId, page, quote, Duplicate);
        }

        var flag = Text(node, "flag") == DraftItemFlags.Found && Quoted(value, quote!) ? DraftItemFlags.Found : DraftItemFlags.Interpreted;
        return new CheckedItem(DraftItemKinds.Value, path, json, flag, documentId, page, quote, null, field.InDraft);
    }

    private static CheckedItem RateTable(JsonNode? node, IReadOnlyList<SentDocument> documents)
    {
        var (documentId, page, quote, citation) = Citation(node, documents);
        var rows = (node?["rows"] as JsonArray)?.Select(r => (r as JsonArray)?.Select(c => c is JsonValue v ? Cell(v) : null).ToList()).ToList();
        if (rows is null || rows.Count == 0 || rows.Any(r => r is null || r.Count == 0 || r.Any(c => c is null)))
        {
            return Dropped(DraftItemKinds.RateTable, DraftFields.RateTables, node?["rows"], documentId, page, quote, Malformed);
        }

        var value = node!["rows"];
        if (citation is not null)
        {
            return Dropped(DraftItemKinds.RateTable, DraftFields.RateTables, value, documentId, page, quote, citation);
        }

        var source = documents.Single(d => d.DocumentId == documentId).Extracted.Tables.Where(t => t.Page == page);
        if (!source.Any(t => SameTable(t.Rows, rows!)))
        {
            return Dropped(DraftItemKinds.RateTable, DraftFields.RateTables, value, documentId, page, quote, TableMismatch);
        }

        return new CheckedItem(DraftItemKinds.RateTable, DraftFields.RateTables, Serialise(value!), DraftItemFlags.Found, documentId, page, quote, null, false);
    }

    private static CheckedItem Rule(JsonNode? node, IReadOnlyList<SentDocument> documents)
    {
        var text = Text(node, "text");
        var (documentId, page, quote, citation) = Citation(node, documents);
        var value = text is null ? null : JsonValue.Create(text);
        if (string.IsNullOrWhiteSpace(text))
        {
            return Dropped(DraftItemKinds.Rule, "rule", null, documentId, page, quote, Malformed);
        }

        if (Expression().IsMatch(text))
        {
            return Dropped(DraftItemKinds.Rule, "rule", value, documentId, page, quote, RuleExpression);
        }

        return citation is not null
            ? Dropped(DraftItemKinds.Rule, "rule", value, documentId, page, quote, citation)
            : new CheckedItem(DraftItemKinds.Rule, "rule", Serialise(value!), DraftItemFlags.Interpreted, documentId, page, quote, null, false);
    }

    /// <summary>AIA-018: a case's expected outcome is kept only with a citation that resolves; an uncited case is kept as Interpreted, without one.</summary>
    private static CheckedItem SimulationCase(JsonNode? node, IReadOnlyList<SentDocument> documents)
    {
        var value = node?.DeepClone() as JsonObject;
        value?.Remove("citation");
        value?.Remove("flag");
        if (value is null || value.Count == 0)
        {
            return Dropped(DraftItemKinds.SimulationCase, "simulationCase", null, null, null, null, Malformed);
        }

        if (node?["citation"] is null)
        {
            value.Remove("expected");
            return new CheckedItem(DraftItemKinds.SimulationCase, "simulationCase", Serialise(value), DraftItemFlags.Interpreted, null, null, null, null, false);
        }

        var (documentId, page, quote, citation) = Citation(node, documents);
        return citation is not null
            ? Dropped(DraftItemKinds.SimulationCase, "simulationCase", value, documentId, page, quote, citation)
            : new CheckedItem(DraftItemKinds.SimulationCase, "simulationCase", Serialise(value), DraftItemFlags.Interpreted, documentId, page, quote, null, false);
    }

    /// <summary>The citation's document and page, and the quote when it resolves; else the reason it doesn't (AIA-013).</summary>
    private static (Guid? DocumentId, int? Page, string? Quote, string? Reason) Citation(JsonNode? node, IReadOnlyList<SentDocument> documents)
    {
        var citation = node?["citation"] as JsonObject;
        var number = Integer(citation?["document"]);
        var page = Integer(citation?["page"]);
        var quote = Text(citation, "quote");
        var document = documents.FirstOrDefault(d => d.Number == number);
        if (citation is null || document is null || page is null || string.IsNullOrWhiteSpace(quote))
        {
            return (document?.DocumentId, page, quote is null ? null : Clip(quote, 2000), NoCitation);
        }

        var text = string.Join("\n", document.Extracted.Pages.Where(p => p.Number == page).Select(p => p.Text)
            .Concat(document.Extracted.Tables.Where(t => t.Page == page).SelectMany(t => t.Rows.Select(r => string.Join(" ", r)))));
        return Normalise(text).Contains(Normalise(quote), StringComparison.Ordinal)
            ? (document.DocumentId, page, Clip(quote, 2000), null)
            : (document.DocumentId, page, Clip(quote, 2000), CitationNotFound);
    }

    private static bool Allowed(DraftField field, JsonNode value)
    {
        if (field.Columns is { } columns)
        {
            return value is JsonObject row && row.Count > 0 && row.All(p => columns.Contains(p.Key, StringComparer.Ordinal));
        }

        return field.Path switch
        {
            DraftFields.Name => value is JsonValue v && v.TryGetValue<string>(out var name) && name.Trim().Length is > 0 and <= 100,
            DraftFields.EffectiveFrom => value is JsonValue d && d.TryGetValue<string>(out var date)
                                         && DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            DraftFields.Coverages => value is JsonArray codes && codes.Count > 0
                                     && codes.All(c => c is JsonValue cv && cv.TryGetValue<string>(out var code) && field.Allowed!.Contains(code, StringComparer.Ordinal)),
            _ => field.Allowed is null || (value is JsonValue a && a.TryGetValue<string>(out var s) && field.Allowed.Contains(s, StringComparer.Ordinal)),
        };
    }

    /// <summary>Found: each of the value's words or figures appears in the quote (a list's every item, a row's every value).</summary>
    private static bool Quoted(JsonNode value, string quote)
    {
        var text = Normalise(quote);
        return Leaves(value).All(l => l.Length > 0 && text.Contains(Normalise(l), StringComparison.Ordinal));
    }

    private static IEnumerable<string> Leaves(JsonNode? node) => node switch
    {
        JsonObject o => o.SelectMany(p => Leaves(p.Value)),
        JsonArray a => a.SelectMany(Leaves),
        JsonValue v => [Cell(v) ?? ""],
        _ => [""],
    };

    /// <summary>AIA-015: the same number of rows and columns, and every cell the same, as text or, when both are numbers, as numbers.</summary>
    public static bool SameTable(IReadOnlyList<IReadOnlyList<string>> source, IReadOnlyList<IReadOnlyList<string?>> proposed) =>
        source.Count == proposed.Count
        && source.Zip(proposed).All(rows => rows.First.Count == rows.Second.Count && rows.First.Zip(rows.Second).All(c => SameCell(c.First, c.Second ?? "")));

    private static bool SameCell(string source, string proposed)
    {
        var a = Normalise(source);
        var b = Normalise(proposed);
        return a == b || (Number(a) is { } x && Number(b) is { } y && x == y);
    }

    /// <summary>A cell as a number: a comma before exactly three digits is a separator (§5.1, English only, D-4), and nothing else is read.</summary>
    private static decimal? Number(string text)
    {
        var plain = Thousands().Replace(text, "$1");
        return Regex.IsMatch(plain, @"^-?\d+(\.\d+)?$") && decimal.TryParse(plain, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n)
            ? n
            : null;
    }

    /// <summary>Whitespace collapsed and case folded, so line breaks and spacing the extraction put in don't decide a match.</summary>
    private static string Normalise(string text) => Spaces().Replace(text, " ").Trim().ToLowerInvariant();

    private static string? Cell(JsonValue value) =>
        value.TryGetValue<string>(out var s) ? s
        : value.TryGetValue<decimal>(out var d) ? d.ToString(CultureInfo.InvariantCulture)
        : value.TryGetValue<bool>(out var b) ? (b ? "true" : "false")
        : null;

    private static IEnumerable<JsonNode?> Array(JsonObject root, string name) => root[name] as JsonArray ?? [];

    private static string? Text(JsonNode? node, string name) => node is JsonObject o && o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static int? Integer(JsonNode? node) => node is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    private static string? Serialise(JsonNode value)
    {
        var json = value.ToJsonString();
        return json.Length <= MaxValueLength ? json : null;
    }

    private static CheckedItem Dropped(string kind, string field, JsonNode? value, Guid? documentId, int? page, string? quote, string reason) =>
        new(kind, field, value is null ? null : Serialise(value.DeepClone()), DraftItemFlags.Interpreted, documentId, page, quote, reason, false);

    private static string Clip(string text, int length) => text.Length <= length ? text : text[..length];

    /// <summary>A model may wrap its JSON in a markdown fence; only that is removed, and the rest must still be one JSON object.</summary>
    private static string Unfence(string output)
    {
        var text = output.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal) && text.EndsWith("```", StringComparison.Ordinal) && text.Length > 6)
        {
            text = text[3..^3];
            text = text.StartsWith("json", StringComparison.OrdinalIgnoreCase) ? text[4..] : text;
        }

        return text.Trim();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"(\d),(?=\d{3}(\D|$))")]
    private static partial Regex Thousands();

    // An expression rather than words: comparison or logical operators, a comparison with a figure, a call (a name straight before a bracket,
    // so "age (next birthday)" stays words) or a block.
    [GeneratedRegex(@"==|!=|&&|\|\||=>|<=|>=|[<>=]\s*\d|\b[A-Za-z_][A-Za-z0-9_]*\(|\{|\}")]
    private static partial Regex Expression();
}
