/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Assist.Evaluation
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
using System.Text.Json.Serialization;
using Insys.Assist.Application;
using Insys.Assist.Domain;
using Insys.BuildingBlocks.Application.Ai;
using Insys.BuildingBlocks.Domain;
using Insys.Rating.Contracts;

namespace Insys.Assist.Evaluation;

/// <summary>
/// The AI-2 evaluation set (ai-assist.md §6 and §7; AI Assist SRS §5.3): refusals from the error catalogue's worded codes, and calculations of the sample
/// tenant's products. Sample data only. Generated and checked by OftzaSeedTests, so it always matches the catalogue and the sample products.
/// </summary>
public sealed record EvaluationSet(string Description, IReadOnlyList<RefusalCase> Refusals, IReadOnlyList<CalculationCase> Calculations)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    [JsonIgnore]
    public int Count => Refusals.Count + Calculations.Count;

    public static EvaluationSet Read(string path) =>
        JsonSerializer.Deserialize<EvaluationSet>(File.ReadAllText(path), Json) ?? throw new InvalidOperationException($"{path} holds no evaluation set.");

    public string Write() => JsonSerializer.Serialize(this, Json) + "\n";
}

/// <summary>A refusal, as the catalogue words it, and how many fields were in error.</summary>
public sealed record RefusalCase(string Id, string Code, bool Retryable, string Means, string? Remedy, int FieldsInError)
{
    [JsonIgnore]
    public ErrorCodeEntry Entry => new(Code, 0, Code, Retryable, "", Means, Remedy);
}

/// <summary>A sample product's calculation: its product, versions, premium and lines, as Rating gives them.</summary>
public sealed record CalculationCase(string Id, string SourceDomain, string ProductCode, int ProductVersion, int RatingVersion, decimal Premium, string Currency,
    IReadOnlyList<RecordedCalculationLine> Lines)
{
    [JsonIgnore]
    public RecordedCalculation Calculation =>
        new(Guid.Empty, SourceDomain, "EVALUATION", ProductCode, ProductVersion, RatingVersion, Premium, Currency, DateTimeOffset.UnixEpoch, [], Lines);
}

/// <summary>What a run asked for, kept beside its results so runs can be compared.</summary>
public sealed record RunInfo(string Provider, string Model, string Effort, string InferenceGeo, string? Currency, DateTimeOffset RanAt);

/// <summary>One case's outcome.</summary>
public sealed record CaseResult(string Id, string Kind, string Status, string? Text, IReadOnlyList<string> RejectedNumbers, long InputTokens, long OutputTokens,
    double Seconds, decimal? Cost, string? ServedModel, string? InferenceRegion, string Facts);

/// <summary>
/// Runs every case through <see cref="AssistExchange"/>, the minimiser, model call and verifier a tenant's run uses, one at a time so the latency is each call's
/// own. Nothing is read from or written to a database, and no cache is used: each case is a fresh call.
/// </summary>
public sealed class EvaluationRunner(IAiModelProvider provider, AssistOptions options)
{
    public async Task<IReadOnlyList<CaseResult>> Run(EvaluationSet set, CancellationToken cancellationToken)
    {
        var refusal = options.PromptFor(AssistFeatures.ExplainRefusal) ?? throw new InvalidOperationException("No EXPLAIN_REFUSAL template is configured.");
        var premium = options.PromptFor(AssistFeatures.ExplainPremium) ?? throw new InvalidOperationException("No EXPLAIN_PREMIUM template is configured.");
        var results = new List<CaseResult>();
        foreach (var item in set.Refusals)
        {
            results.Add(await One(item.Id, ExplanationKinds.Refusal, refusal, ExplanationFacts.Refusal(item.Entry, item.FieldsInError), cancellationToken));
        }

        foreach (var item in set.Calculations)
        {
            results.Add(await One(item.Id, ExplanationKinds.Premium, premium, ExplanationFacts.Premium(item.Calculation), cancellationToken));
        }

        return results;
    }

    private async Task<CaseResult> One(string id, string kind, AssistPromptTemplate template, AllowedFacts facts, CancellationToken cancellationToken)
    {
        var exchange = await AssistExchange.Send(provider, options.ModelVersion, template.Version, template.Text, facts, cancellationToken);
        var response = exchange.Response;
        var cost = exchange.InputTokens + exchange.OutputTokens == 0 ? null : options.CostOf(options.ModelVersion, exchange.InputTokens, exchange.OutputTokens);
        return new CaseResult(id, kind, exchange.Status, response?.Text, exchange.Rejected, exchange.InputTokens, exchange.OutputTokens,
            exchange.Elapsed.TotalSeconds, cost?.Amount, response?.ServedModel, response?.InferenceRegion, exchange.Sent.Text);
    }
}

/// <summary>The evaluation's reports: the summary for D-5 and SRS §5.3, and the sheet a domain expert fills in.</summary>
public static class EvaluationReport
{
    /// <summary>SRS AIA-070: AI-2 answers in 10 seconds or less at the 95th percentile.</summary>
    public const double LatencyTargetSeconds = 10;

    public static string Summary(IReadOnlyList<CaseResult> results, AssistOptions options, string provider, DateTimeOffset ranAt)
    {
        var text = new StringBuilder();
        var model = options.ModelVersion;
        text.AppendLine("# AI-2 evaluation run");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"- **Ran:** {ranAt:yyyy-MM-dd HH:mm} UTC, provider `{provider}`, model asked `{model}`, on sample data only.");
        text.AppendLine(CultureInfo.InvariantCulture, $"- **Prompt versions:** {string.Join(", ", options.Prompts.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"`{p.Key}` {p.Value.Version}"))}.");
        text.AppendLine(CultureInfo.InvariantCulture, $"- **Models that answered, as the provider reported them:** {Listed(results.Select(r => r.ServedModel))}.");
        text.AppendLine(CultureInfo.InvariantCulture, $"- **Where inference ran, as the provider reported it (AIA-050):** {Listed(results.Select(r => r.InferenceRegion))}.");
        text.AppendLine(options.Prices.TryGetValue(model, out var price)
            ? string.Create(CultureInfo.InvariantCulture, $"- **Price list used:** {price.Currency} {price.InputPerMillionTokens} per million input tokens and {price.OutputPerMillionTokens} per million output tokens. {price.Source}.")
            : "- **Price list used:** none is configured for this model, so no cost is given.");
        text.AppendLine("- **Cache:** each case is a fresh call, so the evaluation measures no cache hits. The rate in use comes from tenants' runs (AIA-036).");
        text.AppendLine();
        text.AppendLine("| Kind | Cases | Verified | Rejected by the verifier | Unavailable | Input tokens, mean | Output tokens, mean | Cost per explanation, mean | Median (s) | p95 (s) | Slowest (s) |");
        text.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var group in new[] { ("All", results) }.Concat(results.GroupBy(r => r.Kind).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => (g.Key, (IReadOnlyList<CaseResult>)[.. g]))))
        {
            text.AppendLine(Row(group.Item1, group.Item2, price?.Currency));
        }

        var answered = results.Where(r => r.Status != AssistRunStatuses.Unavailable).ToList();
        var p95 = Percentile(answered.Select(r => r.Seconds), 95);
        text.AppendLine();
        text.AppendLine("## Against the targets (AI Assist SRS §5.3, AIA-070)");
        text.AppendLine(CultureInfo.InvariantCulture, $"- **Explanations with a number not in the source, shown:** 0, by construction: the verifier rejected {results.Count(r => r.Status == AssistRunStatuses.Rejected)} and none of those is shown.");
        text.AppendLine(CultureInfo.InvariantCulture, $"- **Response time at the 95th percentile:** {(p95 is { } p ? p.ToString("0.00", CultureInfo.InvariantCulture) + " s" : "not measured")}, against {LatencyTargetSeconds} s: {(p95 is null ? "no answer to measure" : p95 <= LatencyTargetSeconds ? "met" : "not met")}.");
        text.AppendLine("- **Correct and complete (95% or more), easy to understand (90% or more), revealing nothing the user can't see (0):** a domain expert's review, in `review.csv`. Not measured by this run.");
        return text.ToString();
    }

    /// <summary>One row per case for the domain expert (SRS §5.3), with the facts sent and the answer, and empty columns to fill in.</summary>
    public static string ReviewSheet(IReadOnlyList<CaseResult> results)
    {
        var sheet = new StringBuilder("case,kind,status,rejected_numbers,facts_sent,explanation,correct_and_complete (Y/N),easy_to_understand (Y/N),reveals_data_the_user_cannot_see (Y/N),reviewer_notes\n");
        foreach (var r in results)
        {
            sheet.AppendLine(string.Join(',', Csv(r.Id), Csv(r.Kind), Csv(r.Status), Csv(string.Join(' ', r.RejectedNumbers)), Csv(r.Facts), Csv(r.Text ?? ""), "", "", "", ""));
        }

        return sheet.ToString();
    }

    private static string Row(string name, IReadOnlyList<CaseResult> rows, string? currency)
    {
        var answered = rows.Where(r => r.Status != AssistRunStatuses.Unavailable).ToList();
        // Cost per explanation: what every call cost, those that ended unavailable included, over the explanations given.
        var costs = rows.Where(r => r.Cost is not null).Select(r => r.Cost!.Value).ToList();
        string Mean(IEnumerable<long> values) => answered.Count == 0 ? "-" : values.Average().ToString("0", CultureInfo.InvariantCulture);
        var cost = costs.Count == 0 || currency is null || answered.Count == 0 ? "-" : $"{currency} {(costs.Sum() / answered.Count).ToString("0.000000", CultureInfo.InvariantCulture)}";
        string Seconds(double? value) => value is { } v ? v.ToString("0.00", CultureInfo.InvariantCulture) : "-";
        return string.Create(CultureInfo.InvariantCulture,
            $"| {name} | {rows.Count} | {rows.Count(r => r.Status == AssistRunStatuses.Verified)} | {rows.Count(r => r.Status == AssistRunStatuses.Rejected)} | {rows.Count(r => r.Status == AssistRunStatuses.Unavailable)} | {Mean(answered.Select(r => r.InputTokens))} | {Mean(answered.Select(r => r.OutputTokens))} | {cost} | {Seconds(Percentile(answered.Select(r => r.Seconds), 50))} | {Seconds(Percentile(answered.Select(r => r.Seconds), 95))} | {Seconds(Percentile(answered.Select(r => r.Seconds), 100))} |");
    }

    /// <summary>
    /// The models side by side (Ade, 15 November: he picks the model from this): per model, the verified rate, the numbers the verifier rejected, the time per
    /// answer (median and slowest) against AIA-070's 10 seconds, and the cost per explanation, from each run's folder (results.json and run.json).
    /// </summary>
    public static string Comparison(IReadOnlyList<(RunInfo Run, IReadOnlyList<CaseResult> Results)> runs)
    {
        var text = new StringBuilder("# AI-2 evaluation: the models compared\n\n");
        text.AppendLine("Sample data only, the same set for each model, one call at a time. Times are each call's own; the target is 10 seconds at the 95th percentile (AIA-070).");
        text.AppendLine();
        text.AppendLine("| Model | Effort | Region asked | Regions reported | Cases | Verified | Rejected | Unavailable | Median (s) | p95 (s) | Slowest (s) | Over 10 s | Cost per explanation | Cost of the run |");
        text.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var (run, results) in runs.OrderBy(r => r.Run.Model, StringComparer.Ordinal))
        {
            var answered = results.Where(r => r.Status != AssistRunStatuses.Unavailable).ToList();
            var seconds = answered.Select(r => r.Seconds).ToList();
            var total = results.Where(r => r.Cost is not null).Sum(r => r.Cost!.Value);
            string Share(int n) => results.Count == 0 ? "-" : string.Create(CultureInfo.InvariantCulture, $"{n} ({100.0 * n / results.Count:0.0}%)");
            string S(double? v) => v is { } x ? x.ToString("0.00", CultureInfo.InvariantCulture) : "-";
            var perExplanation = answered.Count == 0 || run.Currency is null ? "-" : string.Create(CultureInfo.InvariantCulture, $"{run.Currency} {total / answered.Count:0.000000}");
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| `{run.Model}` | {run.Effort} | {(run.InferenceGeo.Length == 0 ? "none" : run.InferenceGeo)} | {Listed(results.Select(r => r.InferenceRegion))} | {results.Count} | {Share(results.Count(r => r.Status == AssistRunStatuses.Verified))} | {Share(results.Count(r => r.Status == AssistRunStatuses.Rejected))} | {Share(results.Count(r => r.Status == AssistRunStatuses.Unavailable))} | {S(Percentile(seconds, 50))} | {S(Percentile(seconds, 95))} | {S(Percentile(seconds, 100))} | {seconds.Count(x => x > LatencyTargetSeconds)} | {perExplanation} | {(run.Currency is null ? "-" : $"{run.Currency} {total:0.0000}")} |"));
        }

        text.AppendLine();
        text.AppendLine("## Numbers the verifier rejected, by model");
        foreach (var (run, results) in runs.OrderBy(r => r.Run.Model, StringComparer.Ordinal))
        {
            var rejected = results.Where(r => r.Status == AssistRunStatuses.Rejected).ToList();
            text.AppendLine(CultureInfo.InvariantCulture, $"- **`{run.Model}`:** {(rejected.Count == 0 ? "none" : string.Join("; ", rejected.Select(r => $"{r.Id} ({string.Join(", ", r.RejectedNumbers)})")))}.");
        }

        text.AppendLine();
        text.AppendLine("Correctness, clarity and what an explanation reveals need the domain expert's review of each model's `review.csv` (SRS §5.3); this table doesn't measure them.");
        return text.ToString();
    }

    /// <summary>The nearest-rank percentile, or null with nothing to rank.</summary>
    public static double? Percentile(IEnumerable<double> values, int percentile)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted.Count == 0 ? null : sorted[Math.Clamp((int)Math.Ceiling(percentile / 100.0 * sorted.Count) - 1, 0, sorted.Count - 1)];
    }

    private static string Listed(IEnumerable<string?> values)
    {
        var counted = values.GroupBy(v => v ?? "not reported").OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"`{g.Key}` ({g.Count()})").ToList();
        return counted.Count == 0 ? "none" : string.Join(", ", counted);
    }

    /// <summary>A quoted cell on one line: each fact, or line of an answer, is separated by " | ", so the sheet opens the same in any tool.</summary>
    private static string Csv(string value) =>
        "\"" + value.ReplaceLineEndings(" | ").Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
