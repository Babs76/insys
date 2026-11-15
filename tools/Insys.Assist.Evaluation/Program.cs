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
var set = EvaluationSet.Read(argument("--set", "docs/evaluation/ai2-evaluation-set.json"));
var settings = Settings.Read(argument("--config", "src/Host/Insys.Api/appsettings.json"));
var provider = argument("--provider", "echo");
IAiModelProvider model = provider switch
{
    "claude" => new ClaudeModelProvider(Options.Create(settings.Claude), NullLogger<ClaudeModelProvider>.Instance),
    "echo" => new EchoModelProvider(),
    _ => throw new ArgumentException($"--provider is claude or echo, not {provider}."),
};
if (provider == "echo")
{
    settings.Assist.ModelVersion = "echo";
}

var output = Directory.CreateDirectory(argument("--out", "assist-evaluation")).FullName;
var ranAt = DateTimeOffset.UtcNow;
var results = await new EvaluationRunner(model, settings.Assist).Run(set, CancellationToken.None);
await File.WriteAllTextAsync(Path.Combine(output, "results.json"), JsonSerializer.Serialize(results, EvaluationSet.Json));
await File.WriteAllTextAsync(Path.Combine(output, "summary.md"), EvaluationReport.Summary(results, settings.Assist, provider, ranAt));
await File.WriteAllTextAsync(Path.Combine(output, "review.csv"), EvaluationReport.ReviewSheet(results));
Console.WriteLine($"{results.Count} cases: {results.Count(r => r.Status == "VERIFIED")} verified, {results.Count(r => r.Status == "REJECTED")} rejected, "
    + $"{results.Count(r => r.Status == "UNAVAILABLE")} unavailable. Reports in {output}.");
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
/// A provider for checking the runner itself: it answers with the facts it was sent, so every number is in the source and every answer verifies. Not a model.
/// </summary>
internal sealed class EchoModelProvider : IAiModelProvider
{
    public Task<AiModelResponse> Complete(AiModelRequest request, CancellationToken cancellationToken)
    {
        var text = request.Data.Replace(PromptData.Open, "", StringComparison.Ordinal).Replace(PromptData.Close, "", StringComparison.Ordinal).Trim();
        return Task.FromResult(new AiModelResponse(text, (request.Instructions.Length + request.Data.Length) / 4, text.Length / 4, "echo", null));
    }
}
