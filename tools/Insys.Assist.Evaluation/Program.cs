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
using System.Text.Json;
using Insys.Assist.Application;
using Insys.Assist.Evaluation;
using Insys.BuildingBlocks.Application.Ai;
using Insys.BuildingBlocks.Cloud;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

// The AI-2 evaluation (ai-assist.md §6, batch 7.3): every case of the set through the exchange a tenant's run uses, then the summary for D-5 and the
// domain expert's review sheet. A run against Claude is started by hand, through the assist-eval workflow (Ade the required reviewer); CI runs it with the
// echo provider only, so no real model is called there (AI Assist SRS §9.2). Sample data only.
//   dotnet run --project tools/Insys.Assist.Evaluation -- --set docs/evaluation/ai2-evaluation-set.json --provider echo|claude --out <folder>
var argument = Arguments.Parse(args);
if (Array.IndexOf(args, "--compare") is var at and >= 0)
{
    // --compare <folder> <folder> ... --out <folder>: each folder is one run's reports.
    var folders = args.Skip(at + 1).TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
    var runs = folders.Select(f => (JsonSerializer.Deserialize<RunInfo>(File.ReadAllText(Path.Combine(f, "run.json")), EvaluationSet.Json)!,
        (IReadOnlyList<CaseResult>)JsonSerializer.Deserialize<List<CaseResult>>(File.ReadAllText(Path.Combine(f, "results.json")), EvaluationSet.Json)!)).ToList();
    var into = Directory.CreateDirectory(argument("--out", "assist-comparison")).FullName;
    await File.WriteAllTextAsync(Path.Combine(into, "comparison.md"), EvaluationReport.Comparison(runs));
    Console.WriteLine($"{runs.Count} runs compared in {into}.");
    return 0;
}

if (Array.IndexOf(args, "--ai1") is var ai1At and >= 0)
{
    // --ai1 <set> --out <folder>: AI-1's evaluation (SRS §4.3). Until D-7's documents arrive every measure is WAITING, and no model is called. The real set
    // and its documents live outside this public repository (OQ-191); running against them is added once they are here.
    var ai1 = Insys.Assist.Application.Ai1Evaluation.Read(await File.ReadAllTextAsync(args[ai1At + 1]));
    var measurable = ai1.Cases.Count(c => !c.Invented);
    var into = Directory.CreateDirectory(argument("--out", "assist-ai1-evaluation")).FullName;
    if (measurable > 0)
    {
        // TODO(spec): OQ-191, where D-7's documents and expected values are kept, and so how this job reads them.
        Console.Error.WriteLine($"The set has {measurable} real case(s), but reading D-7's documents isn't built until OQ-191 is decided.");
        return 2;
    }

    var ai1Results = Insys.Assist.Application.Ai1Evaluation.AllWaiting(ai1);
    await File.WriteAllTextAsync(Path.Combine(into, "ai1-measures.json"), JsonSerializer.Serialize(ai1Results, EvaluationSet.Json));
    await File.WriteAllTextAsync(Path.Combine(into, "ai1-measures.md"), "# AI-1 evaluation (SRS §4.3)\n\n| Measure | Target | Status |\n|---|---|---|\n"
        + string.Concat(ai1Results.Select(r => $"| {r.Name} | {(r.Minimum is { } min ? $"at least {min:P0}" : $"at most {r.Maximum}")} | {r.Status}: {r.Outcome} |\n")));
    Console.WriteLine($"AI-1: {ai1Results.Count} measures, all {Insys.Assist.Application.Ai1Evaluation.Waiting} for D-7's documents ({ai1.Line}). No model was called.");
    return 0;
}

var set = EvaluationSet.Read(argument("--set", "docs/evaluation/ai2-evaluation-set.json"));
var settings = Settings.Read(argument("--config", "src/Host/Insys.Api/appsettings.json"));
var provider = argument("--provider", "echo");
IAiModelProvider model = provider switch
{
    "claude" => new ClaudeModelProvider(Options.Create(settings.Claude), NullLogger<ClaudeModelProvider>.Instance),
    "echo" => new EchoModelProvider(),
    _ => throw new ArgumentException($"--provider is claude or echo, not {provider}."),
};
if (provider == "echo" && Environment.GetEnvironmentVariable("Assist__ModelVersion") is null)
{
    // A dry run of the runner: named echo unless a model is named, so a dry run can stand in for that model's run, its prices and the comparison.
    settings.Assist.ModelVersion = "echo";
}

// The tenant's region the run stands for (US or GLOBAL), asked for on every call and checked on every answer, as a tenant's run is (AIA-050).
var region = argument("--region", settings.Claude.InferenceGeo is { Length: > 0 } geo ? geo.ToUpperInvariant() : "GLOBAL");
if (!settings.Assist.Supports(region))
{
    throw new ArgumentException($"--region is one of {string.Join(", ", settings.Assist.InferenceRegions)}, not {region}.");
}

var output = Directory.CreateDirectory(argument("--out", "assist-evaluation")).FullName;
var ranAt = DateTimeOffset.UtcNow;
var results = await new EvaluationRunner(model, settings.Assist, region).Run(set, CancellationToken.None);
await File.WriteAllTextAsync(Path.Combine(output, "results.json"), JsonSerializer.Serialize(results, EvaluationSet.Json));
var summary = EvaluationReport.Summary(results, settings.Assist, provider, ranAt);
await File.WriteAllTextAsync(Path.Combine(output, "summary.md"), summary);

// The job's log carries the summary and every rejected case with its causes (Ade, 16 November), so they are read without the artifacts. Sample data only.
Console.WriteLine(EvaluationReport.Log(results, summary));
await File.WriteAllTextAsync(Path.Combine(output, "review.csv"), EvaluationReport.ReviewSheet(results));
var price = settings.Assist.Prices.GetValueOrDefault(settings.Assist.ModelVersion);
await File.WriteAllTextAsync(Path.Combine(output, "run.json"), JsonSerializer.Serialize(
    new RunInfo(provider, settings.Assist.ModelVersion, settings.Claude.Effort, region.ToLowerInvariant(), price?.Currency, ranAt), EvaluationSet.Json));
Console.WriteLine($"{results.Count} cases: {results.Count(r => r.Status == "VERIFIED")} verified, {results.Count(r => r.Status == "REJECTED")} rejected, "
    + $"{results.Count(r => r.Status == "RESIDENCY_MISMATCH")} residency mismatches, {results.Count(r => r.Status == "UNAVAILABLE")} unavailable. Reports in {output}.");
return results.Count == set.Count ? 0 : 1;

internal static class Arguments
{
    public static Func<string, string, string> Parse(string[] args) => (name, fallback) =>
    {
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : fallback;
    };
}

/// <summary>Assist's settings from the host's configuration file, then the environment (Assist__ModelVersion, Assist__Claude__ApiKey and so on), as the host reads them.</summary>
internal sealed record Settings(AssistOptions Assist, ClaudeModelOptions Claude)
{
    public static Settings Read(string path)
    {
        using var file = JsonDocument.Parse(File.ReadAllText(path));
        var section = file.RootElement.GetProperty("Assist");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var assist = section.Deserialize<AssistOptions>(options) ?? new AssistOptions();
        var claude = section.TryGetProperty("Claude", out var c) ? c.Deserialize<ClaudeModelOptions>(options) ?? new ClaudeModelOptions() : new ClaudeModelOptions();
        assist.ModelVersion = Environment.GetEnvironmentVariable("Assist__ModelVersion") ?? assist.ModelVersion;
        claude.ApiKey = Environment.GetEnvironmentVariable("Assist__Claude__ApiKey") ?? claude.ApiKey;
        claude.InferenceGeo = Environment.GetEnvironmentVariable("Assist__Claude__InferenceGeo") ?? claude.InferenceGeo;
        claude.Effort = Environment.GetEnvironmentVariable("Assist__Claude__Effort") ?? claude.Effort;
        return new Settings(assist, claude);
    }
}

/// <summary>
/// A provider for checking the runner itself: it answers with the facts it was sent, so every number is in the source and every answer verifies, and reports
/// inference where it was asked to run, or global, as Claude's API does. Not a model.
/// </summary>
internal sealed class EchoModelProvider : IAiModelProvider
{
    public Task<AiModelResponse> Complete(AiModelRequest request, CancellationToken cancellationToken)
    {
        var text = request.Data.Replace(PromptData.Open, "", StringComparison.Ordinal).Replace(PromptData.Close, "", StringComparison.Ordinal).Trim();
        return Task.FromResult(new AiModelResponse(text, (request.Instructions.Length + request.Data.Length) / 4, text.Length / 4, "echo", request.InferenceGeo ?? "global"));
    }
}
