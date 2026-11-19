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
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Insys.Assist.Domain;

namespace Insys.Assist.Application;

/// <summary>How a mapped column is checked against its citation (AIA-087: numbers and codes are copied, never computed).</summary>
public enum MappedColumnKind
{
    /// <summary>The tenant's own words or code: it must be in the quote.</summary>
    Copied,

    /// <summary>An amount: a number that must be in the quote, and that needs its currency (a limit means nothing without it).</summary>
    Amount,

    /// <summary>The amount's currency: three letters that must be in the quote.</summary>
    Currency,

    /// <summary>One of INSYS's own codes, chosen by the AI from the values allowed (or checked by the table's import); not in the tenant's words.</summary>
    InsysCode,

    /// <summary>Yes or no.</summary>
    Flag,
}

public sealed record MappedColumn(string Name, MappedColumnKind Kind, bool Required, IReadOnlyList<string>? Allowed = null);

/// <summary>A table AI-3 maps a tenant's documents onto (AIA-083, OQ-186 (a)): its columns and how the row's code is made from them.</summary>
public sealed record SetupMappingTarget(string Table, string Class, string Document, IReadOnlyList<MappedColumn> Columns, Func<JsonObject, string?> Code);

/// <summary>
/// AIA-083's three tables, as Ade decided on 19 November 2026 (OQ-186 (a)): the tenant's chart of accounts, its approval matrix and its role list are the
/// tenant's own facts, mappable whatever their class; every other regulated value stays overlay-only. A role title maps onto a role template only, never
/// onto permissions (OQ-188). Mapped rows are added beside the pack's (OQ-188).
/// </summary>
public static class SetupMappingTargets
{
    public const string ChartOfAccounts = "finance.mst_chart_of_accounts";
    public const string ApprovalAuthority = "workflow.cfg_delegation_and_approval_authority";
    public const string Roles = "iam.cfg_roles_and_permission_sets";

    /// <summary>The account types finance.mst_chart_of_accounts allows (its check constraint).</summary>
    public static readonly string[] AccountTypes = ["ASSET", "LIABILITY", "EQUITY", "INCOME", "EXPENSE"];

    public static IReadOnlyList<SetupMappingTarget> For(IReadOnlyList<string> roleTemplates) =>
    [
        new(ChartOfAccounts, "FINANCE", "chart of accounts",
            [
                new("account_code", MappedColumnKind.Copied, true), new("name", MappedColumnKind.Copied, true), new("type", MappedColumnKind.InsysCode, true, AccountTypes),
                new("gl_account", MappedColumnKind.Copied, true), new("status", MappedColumnKind.InsysCode, true, ["ACTIVE"]),
            ],
            row => Text(row, "account_code") is { Length: > 0 } code ? $"ACC-{code}" : null),
        new(ApprovalAuthority, "FINANCE", "approval matrix",
            [
                new("role_code", MappedColumnKind.InsysCode, true), new("transaction_type", MappedColumnKind.InsysCode, true),
                new("limit_value", MappedColumnKind.Amount, true), new("currency", MappedColumnKind.Currency, true), new("delegation_allowed", MappedColumnKind.Flag, true),
            ],
            row => Text(row, "transaction_type") is { Length: > 0 } type && Text(row, "role_code") is { Length: > 0 } role ? $"AUTH-{type}-{role}" : null),
        new(Roles, "OPERATIONAL", "role list",
            [new("role_code", MappedColumnKind.InsysCode, true, roleTemplates), new("title", MappedColumnKind.Copied, true)],
            row => Text(row, "role_code")),
    ];

    internal static string? Text(JsonObject row, string column) => row[column] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

/// <summary>A pack row the AI may tailor (AIA-082, OQ-187 (a)): its draft value now, the columns it may change, and the answers' values that may set them.</summary>
public sealed record TailorableRow(string Table, string Code, string Values, IReadOnlyList<string> Columns, IReadOnlyDictionary<string, IReadOnlyList<string>> Answers);

public sealed record TailoredValue(string Table, string Code, string Values, string Answer, string AnswerValue);

public sealed record MappedValue(string Table, string Code, string Class, string Values, Guid DocumentId, int Page, string Quote);

public sealed record UnmappedLine(Guid? DocumentId, int? Page, string? Quote, string? Table, string Reason);

public sealed record SetupAiResult(IReadOnlyList<TailoredValue> Tailored, IReadOnlyList<MappedValue> Mapped, IReadOnlyList<UnmappedLine> Unmapped);

/// <summary>What AI-3 sends (AIA-088: business configuration only): the tailorable rows with their answers, the tables it may map onto, and the documents' text.</summary>
public static class SetupAiPayload
{
    public static string Render(IReadOnlyList<TailorableRow> rows, IReadOnlyList<SetupMappingTarget> targets, IReadOnlyList<SentDocument> documents) => new JsonObject
    {
        ["tailorable"] = new JsonArray([.. rows.Select(r => (JsonNode)new JsonObject
        {
            ["table"] = r.Table, ["code"] = r.Code, ["values"] = JsonNode.Parse(r.Values),
            ["columns"] = new JsonArray([.. r.Columns.Select(c => (JsonNode)c)]),
            ["answers"] = new JsonObject(r.Answers.Select(a => KeyValuePair.Create(a.Key, (JsonNode?)new JsonArray([.. a.Value.Select(v => (JsonNode)v)])))),
        })]),
        ["targets"] = new JsonArray([.. targets.Select(t => (JsonNode)new JsonObject
        {
            ["table"] = t.Table, ["document"] = t.Document,
            ["columns"] = new JsonArray([.. t.Columns.Select(c => (JsonNode)new JsonObject
            {
                ["column"] = c.Name, ["kind"] = c.Kind.ToString(), ["required"] = c.Required,
                ["allowed"] = c.Allowed is null ? null : new JsonArray([.. c.Allowed.Select(a => (JsonNode)a)]),
            })]),
        })]),
        ["documents"] = new JsonArray([.. documents.Select(d => (JsonNode)new JsonObject
        {
            ["document"] = d.Number,
            ["pages"] = new JsonArray([.. d.Extracted.Pages.Select(p => (JsonNode)new JsonObject { ["page"] = p.Number, ["text"] = p.Text })]),
            ["tables"] = new JsonArray([.. d.Extracted.Tables.Select(t => (JsonNode)new JsonObject
            {
                ["page"] = t.Page, ["table"] = t.Index, ["rows"] = new JsonArray([.. t.Rows.Select(r => (JsonNode)new JsonArray([.. r.Select(c => (JsonNode)c)]))]),
            })]),
        })]),
    }.ToJsonString();
}

/// <summary>
/// INSYS's deterministic checks on what AI-3's model returned (AIA-082, AIA-083, AIA-084, AIA-087, OQ-186 to OQ-188): nothing it says is trusted.
/// <list type="bullet">
/// <item>A tailored value is kept only for a row the pack marks tailorable, in a column it names, from an answer it names, equal to one of that answer's values.</item>
/// <item>A mapped value is kept only on one of the three tables, with only their columns and every required one; its quote must be on the cited page of a
/// document sent; every number, the currency and the tenant's own words and codes must be in the quote; INSYS's codes must be among those allowed.</item>
/// <item>A mapped row whose code is a pack row's is dropped (CODE_IN_PACK): mapped rows are added beside the pack's, never over them (OQ-188).</item>
/// <item>Everything dropped is listed with why, beside what the model said it couldn't map (AIA-083).</item>
/// </list>
/// The output must be one JSON object; anything else rejects the pass (ASSIST_OUTPUT_REJECTED).
/// </summary>
public static partial class SetupAiCheck
{
    public const string NotTailorable = "NOT_TAILORABLE";
    public const string ColumnNotTailorable = "COLUMN_NOT_TAILORABLE";
    public const string AnswerNotNamed = "ANSWER_NOT_NAMED";
    public const string NotInAnswer = "NOT_IN_ANSWER";
    public const string UnknownTable = "UNKNOWN_TABLE";
    public const string UnknownColumn = "UNKNOWN_COLUMN";
    public const string MissingColumn = "MISSING_COLUMN";
    public const string NotAllowed = "NOT_ALLOWED";
    public const string NotInQuote = "NOT_IN_QUOTE";
    public const string CurrencyMissing = "CURRENCY_MISSING";
    public const string CodeInPack = "CODE_IN_PACK";
    public const string Duplicate = "DUPLICATE";
    public const string Malformed = "MALFORMED";
    public const string NoCitation = DraftProposalCheck.NoCitation;
    public const string CitationNotFound = DraftProposalCheck.CitationNotFound;
    public const string CitationTooShort = DraftProposalCheck.CitationTooShort;

    /// <summary>At most this many proposals of each kind are read, so an output can't flood the pass.</summary>
    public const int MaxItems = 400;

    public static SetupAiResult? Check(string output, IReadOnlyList<TailorableRow> rows, IReadOnlyList<SetupMappingTarget> targets, IReadOnlyList<SentDocument> documents,
        IReadOnlySet<(string Table, string Code)> packCodes)
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

        var unmapped = new List<UnmappedLine>();
        var tailored = Tailored(root["tailored"] as JsonArray ?? [], rows, unmapped);
        var mapped = Mapped(root["mapped"] as JsonArray ?? [], targets, documents, packCodes, unmapped);
        foreach (var node in (root["unmapped"] as JsonArray ?? []).Take(MaxItems))
        {
            var number = Integer(node?["document"]);
            var document = documents.FirstOrDefault(d => d.Number == number);
            unmapped.Add(new UnmappedLine(document?.DocumentId, document is null ? null : Integer(node?["page"]), Clip(Text(node, "quote")), Clip(Text(node, "table"), 80),
                UnmappedReasons.NotMapped));
        }

        return new SetupAiResult(tailored, mapped, unmapped);
    }

    private static List<TailoredValue> Tailored(JsonArray proposals, IReadOnlyList<TailorableRow> rows, List<UnmappedLine> unmapped)
    {
        var byRow = rows.ToDictionary(r => (r.Table, r.Code));
        var kept = new Dictionary<(string, string), TailoredValue>();
        foreach (var node in proposals.Take(MaxItems))
        {
            var (table, code, column, answer) = (Text(node, "table") ?? "", Text(node, "code") ?? "", Text(node, "column") ?? "", Text(node, "answer") ?? "");
            var value = node?["value"] is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;
            string? Drop(string reason)
            {
                unmapped.Add(new UnmappedLine(null, null, value is null ? null : Clip($"{code}.{column} = {value}"), Clip(table, 80), reason));
                return null;
            }

            if (!byRow.TryGetValue((table, code), out var row))
            {
                Drop(NotTailorable);
            }
            else if (!row.Columns.Contains(column, StringComparer.Ordinal))
            {
                Drop(ColumnNotTailorable);
            }
            else if (!row.Answers.TryGetValue(answer, out var held))
            {
                Drop(AnswerNotNamed);
            }
            else if (value is null || !held.Contains(value, StringComparer.Ordinal))
            {
                // AIA-087: a tailored value is the answer's own, copied; never one the model made up or worked out.
                Drop(NotInAnswer);
            }
            else if (kept.ContainsKey((table, code)))
            {
                // One tailored column a row a pass, so its citation names one answer (AIA-084).
                Drop(Duplicate);
            }
            else
            {
                var values = JsonNode.Parse(row.Values) as JsonObject ?? [];
                values[column] = value;
                kept[(table, code)] = new TailoredValue(table, code, values.ToJsonString(), answer, value);
            }
        }

        return [.. kept.Values];
    }

    private static List<MappedValue> Mapped(JsonArray proposals, IReadOnlyList<SetupMappingTarget> targets, IReadOnlyList<SentDocument> documents,
        IReadOnlySet<(string Table, string Code)> packCodes, List<UnmappedLine> unmapped)
    {
        var byTable = targets.ToDictionary(t => t.Table, StringComparer.Ordinal);
        var kept = new List<MappedValue>();
        var seen = new HashSet<(string, string)>();
        foreach (var node in proposals.Take(MaxItems))
        {
            var table = Text(node, "table") ?? "";
            var (documentId, page, quote, citation) = Citation(node, documents);
            void Drop(string reason) => unmapped.Add(new UnmappedLine(documentId, page, quote, Clip(table, 80), reason));
            if (!byTable.TryGetValue(table, out var target))
            {
                Drop(UnknownTable);
                continue;
            }

            if (node?["value"] is not JsonObject value)
            {
                Drop(Malformed);
                continue;
            }

            if (citation is not null)
            {
                Drop(citation);
                continue;
            }

            if (Columns(value, target, quote!) is { } reason)
            {
                Drop(reason);
                continue;
            }

            if (target.Code(value) is not { Length: > 0 and <= 120 } code)
            {
                Drop(MissingColumn);
                continue;
            }

            if (packCodes.Contains((table, code)))
            {
                Drop(CodeInPack);
                continue;
            }

            if (!seen.Add((table, code)))
            {
                Drop(Duplicate);
                continue;
            }

            kept.Add(new MappedValue(table, code, target.Class, Line(target, value), documentId!.Value, page!.Value, quote!));
        }

        return kept;
    }

    /// <summary>The reason a mapped row's columns fail their checks, or null when every one passes.</summary>
    private static string? Columns(JsonObject value, SetupMappingTarget target, string quote)
    {
        var columns = target.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);
        if (value.Any(p => !columns.ContainsKey(p.Key)))
        {
            return UnknownColumn;
        }

        var text = Normalise(quote);
        foreach (var column in target.Columns)
        {
            var cell = value[column.Name];
            if (cell is null)
            {
                if (column.Required)
                {
                    return column.Kind == MappedColumnKind.Currency ? CurrencyMissing : MissingColumn;
                }

                continue;
            }

            var reason = column.Kind switch
            {
                MappedColumnKind.Flag => cell is JsonValue f && f.TryGetValue<bool>(out _) ? null : Malformed,
                MappedColumnKind.Amount => Amount(cell) is { } amount ? (Numbers(text).Contains(amount) ? null : NotInQuote) : Malformed,
                MappedColumnKind.Currency => cell is JsonValue c && c.TryGetValue<string>(out var currency) && CurrencyCode().IsMatch(currency)
                    ? (Words(text).Contains(currency.ToLowerInvariant()) ? null : NotInQuote)
                    : Malformed,
                MappedColumnKind.InsysCode => cell is JsonValue i && i.TryGetValue<string>(out var insys) && InsysCode().IsMatch(insys)
                    ? (column.Allowed is null || column.Allowed.Contains(insys, StringComparer.Ordinal) ? null : NotAllowed)
                    : Malformed,
                _ => cell is JsonValue w && w.TryGetValue<string>(out var words) && words.Trim().Length is > 0 and <= 100
                    ? (text.Contains(Normalise(words), StringComparison.Ordinal) ? null : NotInQuote)
                    : Malformed,
            };
            if (reason is not null)
            {
                return reason;
            }
        }

        return null;
    }

    /// <summary>The row as its table's import takes it: a role's title is its label, in English (D-4); the other columns as given, amounts as numbers.</summary>
    private static string Line(SetupMappingTarget target, JsonObject value)
    {
        var line = new JsonObject();
        foreach (var column in target.Columns)
        {
            if (value[column.Name] is not { } cell)
            {
                continue;
            }

            if (target.Table == SetupMappingTargets.Roles && column.Name == "title")
            {
                line["labels"] = new JsonObject { ["en"] = cell.GetValue<string>().Trim() };
            }
            else
            {
                line[column.Name] = column.Kind == MappedColumnKind.Amount ? JsonValue.Create(Amount(cell)!.Value) : cell.DeepClone();
            }
        }

        return line.ToJsonString();
    }

    /// <summary>The citation's document and page, and the quote when it resolves; else why it doesn't (AIA-083), as AI-1's citations are checked.</summary>
    private static (Guid? DocumentId, int? Page, string? Quote, string? Reason) Citation(JsonNode? node, IReadOnlyList<SentDocument> documents)
    {
        var citation = node?["citation"] as JsonObject;
        var number = Integer(citation?["document"]);
        var page = Integer(citation?["page"]);
        var quote = Text(citation, "quote");
        var document = documents.FirstOrDefault(d => d.Number == number);
        if (citation is null || document is null || page is null || string.IsNullOrWhiteSpace(quote))
        {
            return (document?.DocumentId, page, Clip(quote), NoCitation);
        }

        var normalised = Normalise(quote);
        if (normalised.Length < DraftProposalCheck.MinQuoteCharacters && normalised.Split(' ').Length < 3)
        {
            return (document.DocumentId, page, Clip(quote), CitationTooShort);
        }

        var text = string.Join("\n", document.Extracted.Pages.Where(p => p.Number == page).Select(p => p.Text)
            .Concat(document.Extracted.Tables.Where(t => t.Page == page).SelectMany(t => t.Rows.Select(r => string.Join(" ", r)))));
        return Normalise(text).Contains(normalised, StringComparison.Ordinal)
            ? (document.DocumentId, page, Clip(quote), null)
            : (document.DocumentId, page, Clip(quote), CitationNotFound);
    }

    /// <summary>An amount as a number: a JSON number, or text of digits with at most a decimal point and thousands commas.</summary>
    private static decimal? Amount(JsonNode cell) => cell switch
    {
        JsonValue v when v.TryGetValue<decimal>(out var d) => d,
        JsonValue v when v.TryGetValue<string>(out var s) => Number(s),
        _ => null,
    };

    /// <summary>Every number written in the quote (§5.1: a comma before exactly three digits is a separator, English only, D-4).</summary>
    private static HashSet<decimal> Numbers(string text) =>
        [.. NumberToken().Matches(text).Select(m => Number(m.Value)).Where(n => n is not null).Select(n => n!.Value)];

    private static decimal? Number(string text)
    {
        var plain = Thousands().Replace(text.Trim(), "$1");
        return PlainNumber().IsMatch(plain) && decimal.TryParse(plain, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n)
            ? n
            : null;
    }

    private static HashSet<string> Words(string text) => [.. WordToken().Matches(text).Select(m => m.Value)];

    private static string Normalise(string text) => Spaces().Replace(text, " ").Trim().ToLowerInvariant();

    private static string? Text(JsonNode? node, string name) => node is JsonObject o && o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static int? Integer(JsonNode? node) => node is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    private static string? Clip(string? text, int length = 2000) => text is null ? null : text.Length <= length ? text : text[..length];

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

    [GeneratedRegex(@"^-?\d+(\.\d+)?$")]
    private static partial Regex PlainNumber();

    [GeneratedRegex(@"-?\d{1,3}(,\d{3})+(\.\d+)?|-?\d+(\.\d+)?")]
    private static partial Regex NumberToken();

    [GeneratedRegex(@"[a-z]+")]
    private static partial Regex WordToken();

    [GeneratedRegex(@"^[A-Z]{3}$")]
    private static partial Regex CurrencyCode();

    [GeneratedRegex(@"^[A-Z][A-Z0-9_]{0,79}$")]
    private static partial Regex InsysCode();
}
