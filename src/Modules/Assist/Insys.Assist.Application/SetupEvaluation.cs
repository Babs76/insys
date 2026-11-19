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
using System.Text.Json;
using System.Text.Json.Nodes;
using Insys.Assist.Domain;
using Insys.BuildingBlocks.Application.Ai;

namespace Insys.Assist.Application;

/// <summary>A mapped row a case expects: its table, its code and the values it must hold (a subset of its line).</summary>
public sealed record ExpectedMapping(string Table, string Code, JsonElement Values);

/// <summary>
/// One AI-3 evaluation case (AI Assist SRS §5A.4, addendum §7: "standard questionnaires and sample tenant documents for AI-3"): a tenant document with the
/// tailorable rows it is run with, the rows an expert expects it mapped onto, and the lines they expect left unmapped.
/// </summary>
/// <param name="CountsTowardTargets">False for an invented sample, which proves the runner but measures nothing: only a tenant's real sample documents count.</param>
public sealed record SetupEvaluationCase(string Id, string Document, IReadOnlyList<TailorableRow> Tailorable, IReadOnlyList<ExpectedMapping> Expected,
    IReadOnlyList<string> ExpectedUnmapped, bool CountsTowardTargets);

/// <summary>A §5A.4 measure: its target, and its status, WAITING until the set holds cases that count.</summary>
public sealed record SetupEvaluationMeasure(string Measure, string Target, string Status, string? WaitingFor);

/// <param name="RoleTemplates">The role template codes the cases' role titles may map onto (OQ-188): INSYS's own, as GET /identity/role-templates lists them.</param>
public sealed record SetupEvaluationSet(string Feature, string Status, string? WaitingFor, IReadOnlyList<SetupEvaluationMeasure> Measures, IReadOnlyList<string> RoleTemplates,
    IReadOnlyList<SetupEvaluationCase> Cases)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static SetupEvaluationSet Read(string path) => JsonSerializer.Deserialize<SetupEvaluationSet>(File.ReadAllText(path), Json)
        ?? throw new InvalidOperationException($"{path} isn't an AI-3 evaluation set.");
}

/// <summary>One case's outcome: what was mapped, how much of it the expert expected, and whether every kept citation resolved.</summary>
/// <param name="AccountsMaskedUnmapped">OQ-189 (decided (a), Ade 19 November 2026): expected accounts left unmapped because the minimiser masked a value of
/// theirs, measured on its own so the decision can be revisited with the tenant samples.</param>
public sealed record SetupCaseResult(string Id, string Status, bool CountsTowardTargets, int Expected, int MappedCorrect, int MappedWrong, int CitationsValid, int Mapped,
    int RegulatedNotFromOverlay, IReadOnlyList<string> Missed, IReadOnlyList<string> Unexpected, int AccountsMaskedUnmapped = 0);

/// <summary>
/// §5A.4's measures on AI-3's output, as INSYS's own check leaves it (<see cref="SetupAiCheck"/>): a kept mapped value has a citation that resolved, so
/// "mapped values with a valid citation" is measured on what is kept; "mapped values correct" compares each kept row with the expert's; "regulated values not
/// from an approved overlay" counts a tailored or mapped regulated row, which the check never lets through.
/// </summary>
public static class SetupEvaluationScore
{
    public static SetupCaseResult Score(SetupEvaluationCase @case, SetupAiResult? result, string status)
    {
        if (result is null)
        {
            return new SetupCaseResult(@case.Id, status, @case.CountsTowardTargets, @case.Expected.Count, 0, 0, 0, 0, 0, [.. @case.Expected.Select(e => $"{e.Table}:{e.Code}")], []);
        }

        var expected = @case.Expected.ToDictionary(e => (e.Table, e.Code));
        var correct = 0;
        var wrong = 0;
        var unexpected = new List<string>();
        foreach (var mapped in result.Mapped)
        {
            if (!expected.TryGetValue((mapped.Table, mapped.Code), out var want))
            {
                unexpected.Add($"{mapped.Table}:{mapped.Code}");
                wrong++;
            }
            else if (Holds(JsonNode.Parse(mapped.Values), JsonNode.Parse(want.Values.GetRawText())))
            {
                correct++;
            }
            else
            {
                wrong++;
            }
        }

        var missed = @case.Expected.Where(e => !result.Mapped.Any(m => m.Table == e.Table && m.Code == e.Code)).Select(e => $"{e.Table}:{e.Code}").ToList();
        var regulated = result.Tailored.Count(t => @case.Tailorable.All(r => r.Table != t.Table || r.Code != t.Code));
        return new SetupCaseResult(@case.Id, status, @case.CountsTowardTargets, @case.Expected.Count, correct, wrong, result.Mapped.Count, result.Mapped.Count, regulated, missed, unexpected);
    }

    /// <summary>
    /// OQ-189's figure: the expected chart-of-accounts rows the case missed, one of whose text values is in the document as extracted but not as minimised, so
    /// the privacy filter, not the model, kept it from being mapped.
    /// </summary>
    public static int AccountsMasked(SetupEvaluationCase @case, SetupCaseResult result, string extracted, string minimised)
    {
        static string N(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim().ToLowerInvariant();
        var (raw, sent) = (N(extracted), N(minimised));
        return @case.Expected.Where(e => e.Table == SetupMappingTargets.ChartOfAccounts && result.Missed.Contains($"{e.Table}:{e.Code}"))
            .Count(e => e.Values.ValueKind == JsonValueKind.Object && e.Values.EnumerateObject()
                .Any(p => p.Value.ValueKind == JsonValueKind.String && N(p.Value.GetString()!) is { Length: > 0 } v && raw.Contains(v, StringComparison.Ordinal)
                          && !sent.Contains(v, StringComparison.Ordinal)));
    }

    /// <summary>Whether a mapped line holds every value the expert expects (numbers compared as numbers).</summary>
    private static bool Holds(JsonNode? line, JsonNode? want) => want switch
    {
        JsonObject o => line is JsonObject l && o.All(p => Holds(l[p.Key], p.Value)),
        JsonValue v when v.TryGetValue<decimal>(out var n) => line is JsonValue lv && lv.TryGetValue<decimal>(out var m) && m == n,
        _ => JsonNode.DeepEquals(line, want),
    };
}

/// <summary>
/// The AI-3 evaluation's runner (7.8c): each case's document read, checked and minimised as a pass reads it, the call a pass makes, and INSYS's check, scored
/// against the expert's rows. Against a real model it is started by hand, as AI-2's is (SRS §9.2: no real model in CI). With the set WAITING, it proves the runner
/// only: an invented sample counts toward no target.
/// </summary>
public sealed class SetupEvaluationRunner(IAiModelProvider provider, AssistOptions options, IDraftDocumentExtractor extractor, IReadOnlyList<string> roleTemplates,
    string region = AssistRegions.Global)
{
    public async Task<IReadOnlyList<SetupCaseResult>> Run(SetupEvaluationSet set, string root, CancellationToken cancellationToken)
    {
        var template = options.PromptFor(AssistFeatures.TenantSetup) ?? throw new InvalidOperationException("No prompt template is configured for TENANT_SETUP (Assist:Prompts).");
        var targets = SetupMappingTargets.For(roleTemplates);
        var results = new List<SetupCaseResult>();
        foreach (var @case in set.Cases)
        {
            var content = await File.ReadAllBytesAsync(Path.Combine(root, @case.Document), cancellationToken);
            var extracted = DraftDocumentKinds.Detect(content) is { } kind ? extractor.Extract(kind, content) : null;
            if (extracted is null || IdentifierCheck.Find(extracted).Count > 0)
            {
                results.Add(SetupEvaluationScore.Score(@case, null, extracted is null ? AssistErrorCodes.SourceUnreadable : AssistErrorCodes.DocumentIdentifiers));
                continue;
            }

            var sent = new SentDocument(1, Guid.Empty, Path.GetFileName(@case.Document), extracted with
            {
                Pages = extracted.Pages.Select(p => p with { Text = Minimiser.Minimise(p.Text).Text }).ToList(),
                Tables = extracted.Tables.Select(t => t with { Rows = t.Rows.Select(r => (IReadOnlyList<string>)r.Select(c => Minimiser.Minimise(c).Text).ToList()).ToList() }).ToList(),
            });
            AiModelResponse? response = null;
            try
            {
                response = await provider.Complete(new AiModelRequest(options.ModelVersion, template.Version, template.Text,
                    PromptData.Delimit(SetupAiPayload.Render(@case.Tailorable, targets, [sent])), region.ToLowerInvariant()), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Counted as unavailable; nothing of the document is logged.
            }

            var ok = response is not null && AssistRegions.Allows(region, response.InferenceRegion);
            var result = ok ? SetupAiCheck.Check(response!.Text, @case.Tailorable, targets, [sent], new HashSet<(string, string)>()) : null;
            var scored = SetupEvaluationScore.Score(@case, result, response is null ? AssistRunStatuses.Unavailable
                : !ok ? AssistRunStatuses.ResidencyMismatch : result is null ? AssistRunStatuses.Rejected : AssistRunStatuses.Verified);
            results.Add(scored with { AccountsMaskedUnmapped = SetupEvaluationScore.AccountsMasked(@case, scored, Text(extracted), Text(sent.Extracted)) });
        }

        return results;
    }

    private static string Text(ExtractedDocument document) =>
        string.Join("\n", document.Pages.Select(p => p.Text).Concat(document.Tables.SelectMany(t => t.Rows.Select(r => string.Join(" ", r)))));

    /// <summary>The §5A.4 measures from the cases that count; a measure with none stays WAITING, whatever the invented samples gave.</summary>
    public static IReadOnlyList<SetupEvaluationMeasure> Measures(SetupEvaluationSet set, IReadOnlyList<SetupCaseResult> results)
    {
        var counted = results.Where(r => r.CountsTowardTargets).ToList();
        if (counted.Count == 0)
        {
            return set.Measures;
        }

        var mapped = counted.Sum(r => r.Mapped);
        string Rate(int part) => mapped == 0 ? "no mapped values" : $"{100.0 * part / mapped:F1}%";
        return
        [
            .. set.Measures.Select(m => m.Measure switch
            {
                "MAPPED_VALUES_CORRECT" => m with { Status = Rate(counted.Sum(r => r.MappedCorrect)), WaitingFor = null },
                "MAPPED_VALUES_WITH_VALID_CITATION" => m with { Status = Rate(counted.Sum(r => r.CitationsValid)), WaitingFor = null },
                "ACCOUNTS_UNMAPPED_BY_PRIVACY_MASK" => m with { Status = counted.Sum(r => r.AccountsMaskedUnmapped).ToString(System.Globalization.CultureInfo.InvariantCulture), WaitingFor = null },
                "REGULATED_VALUES_NOT_FROM_APPROVED_OVERLAY" => m with { Status = counted.Sum(r => r.RegulatedNotFromOverlay).ToString(System.Globalization.CultureInfo.InvariantCulture), WaitingFor = null },
                _ => m,
            }),
        ];
    }
}
