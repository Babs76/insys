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
using Insys.Assist.Domain;
using Insys.BuildingBlocks.Application.Ai;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;
using Microsoft.Extensions.Options;

namespace Insys.Assist.Application;

public sealed class AssistOptions
{
    /// <summary>The model asked, from configuration (AIA-074): "stub" where no real model is connected.</summary>
    public string ModelVersion { get; set; } = "stub";

    /// <summary>The cap for a tenant the operator hasn't set one for: low until batch 7.3's measured cost (addendum §6).</summary>
    public long DefaultMonthlyTokenCap { get; set; } = 100_000;

    /// <summary>
    /// The prompt templates, by feature, as configuration (AIA-074; SRS §7.1): each a version and its text, INSYS's own words with no record data (AIA-061).
    /// A change of text needs a new version, and goes through the evaluation set before release.
    /// </summary>
    public Dictionary<string, AssistPromptTemplate> Prompts { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The regions a tenant may name for inference, in the provider's terms (AIA-050): US and GLOBAL for Claude today. A tenant's calls ask for its region, and an
    /// answer counts only if the provider reports running there (GLOBAL accepts any).
    /// </summary>
    public string[] InferenceRegions { get; set; } = [];

    /// <summary>Whether a tenant may name this region.</summary>
    public bool Supports(string? region) => region is { Length: > 0 } && InferenceRegions.Contains(region, StringComparer.OrdinalIgnoreCase);

    /// <summary>The provider's list prices, by model, for each run's cost (D-5, AIA-075). A model with none has no cost recorded.</summary>
    public Dictionary<string, AssistModelPrice> Prices { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The template configured for a feature, or null when there is none.</summary>
    public AssistPromptTemplate? PromptFor(string feature) =>
        Prompts.TryGetValue(feature, out var template) && template is { Version.Length: > 0, Text.Length: > 0 } ? template : null;

    /// <summary>A run's cost at the configured list price, rounded to six places, with its currency; null when the model has no price.</summary>
    public (decimal Amount, string Currency)? CostOf(string model, long inputTokens, long outputTokens) =>
        Prices.TryGetValue(model, out var price) && price.Currency is { Length: 3 }
            ? (Math.Round((inputTokens * price.InputPerMillionTokens + outputTokens * price.OutputPerMillionTokens) / 1_000_000m, 6, MidpointRounding.ToEven), price.Currency)
            : null;
}

/// <summary>One prompt template: its version, recorded on every run and in the cache key (AIA-036), and its text.</summary>
public sealed class AssistPromptTemplate
{
    public string Version { get; set; } = "";

    public string Text { get; set; } = "";
}

/// <summary>A model's list price per million tokens, with its currency and where it was taken from (D-5).</summary>
public sealed class AssistModelPrice
{
    public decimal InputPerMillionTokens { get; set; }

    public decimal OutputPerMillionTokens { get; set; }

    public string Currency { get; set; } = "";

    /// <summary>The price list used and its date, as text, for the cost report.</summary>
    public string Source { get; set; } = "";
}

/// <summary>What one exchange with the model gave: the payload sent, the answer (null when the provider failed), the numbers rejected and the status.</summary>
/// <param name="InputTokens">What the call used, from the answer, or from the provider's failure when it says (a refusal or an answer cut short is still billed).</param>
public sealed record AssistExchangeResult(MinimisedPayload Sent, AiModelResponse? Response, IReadOnlyList<string> Rejected, string Status, TimeSpan Elapsed, long InputTokens = 0,
    long OutputTokens = 0);

/// <summary>
/// The exchange itself, with nothing read or written (AIA-072): the minimiser, the call and the verifier. The runner and the evaluation (batch 7.3) both use it, so
/// the evaluation measures exactly what a tenant's run does.
/// </summary>
public static class AssistExchange
{
    /// <param name="inferenceGeo">Where the call asks inference to run (the tenant's region in the provider's terms); null to leave it to the deployment.</param>
    public static async Task<AssistExchangeResult> Send(IAiModelProvider provider, string model, string promptVersion, string instructions, AllowedFacts facts,
        CancellationToken cancellationToken, string? inferenceGeo = null)
    {
        // A second layer behind the allowed fields (addendum §5.4): the minimiser masks identifiers a structured field could still carry.
        var minimised = Minimiser.Minimise(facts.Render());
        var timer = System.Diagnostics.Stopwatch.StartNew();
        AiModelResponse? response = null;
        long failedInput = 0, failedOutput = 0;
        try
        {
            response = await provider.Complete(new AiModelRequest(model, promptVersion, instructions, PromptData.Delimit(minimised.Text), inferenceGeo), cancellationToken);
        }
        catch (AiModelUnavailableException ex)
        {
            // UNAVAILABLE (P8), with what the call used when the provider says, so the cap and the cost still count it (AIA-073, AIA-075).
            (failedInput, failedOutput) = (ex.InputTokens, ex.OutputTokens);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Recorded by the caller as UNAVAILABLE: INSYS never waits on the AI (P8), whatever the provider threw.
        }

        timer.Stop();
        // Against the facts' values only, every amount with its currency, so a stated currency must match (addendum §5.1).
        var rejected = response is null ? [] : NumberVerifier.Verify(response.Text, Minimiser.Minimise(facts.Values()).Text, strictCurrency: true, facts.StepNumbers).Rejected;
        var status = response is null ? AssistRunStatuses.Unavailable : rejected.Count > 0 ? AssistRunStatuses.Rejected : AssistRunStatuses.Verified;
        return new AssistExchangeResult(minimised, response, rejected, status, timer.Elapsed, response?.InputTokens ?? failedInput, response?.OutputTokens ?? failedOutput);
    }
}

/// <param name="Instructions">The prompt template's text (<see cref="AssistOptions.Prompts"/>): INSYS's own words, never record data (AIA-061).</param>
/// <param name="Facts">The structured facts, the only data sent (D-15), and what the verifier compares numbers against.</param>
public sealed record AssistRunRequest(string Feature, string PromptVersion, string Instructions, AllowedFacts Facts, Guid? CorrelationId = null);

/// <param name="Text">The verified output, or null: the caller then shows the plain data (AIA-033), or carries on without (P8).</param>
public sealed record AssistRunOutcome(Guid RunId, string Status, string? Text, IReadOnlyList<string> RejectedNumbers);

public sealed record AssistUsage(long Tokens, long Runs, long Rejected, long Unavailable);

/// <summary>A model call made and checked, not yet recorded: its run, its payload and how it ended.</summary>
public sealed record AssistCall(AssistRun Run, AssistRunPayload Payload, AssistRunOutcome Outcome);

/// <summary>Whether a run may be made: the settings allow it, and whether the cap is reached (checked only before a call, so a reused explanation still shows).</summary>
/// <param name="InferenceRegion">Where the tenant's setting allows inference to run (AIA-050), in the provider's terms: US or GLOBAL.</param>
public sealed record AssistAllowance(bool CapReached, string InferenceRegion = "")
{
    /// <summary>The region a call asks for, in the provider's terms (inference_geo).</summary>
    public string InferenceGeo => InferenceRegion.ToLowerInvariant();

    public bool Allows(string? reported) => AssistRegions.Allows(InferenceRegion, reported);
}

/// <summary>The residency rule on a call's answer (AIA-050, Ade 15 November).</summary>
public static class AssistRegions
{
    public const string Global = "GLOBAL";

    /// <summary>GLOBAL accepts any region the provider reports; any other region only itself, letter case aside. A call that reports none can't show where it ran.</summary>
    public static bool Allows(string allowed, string? reported) =>
        reported is { Length: > 0 } && (string.Equals(allowed, Global, StringComparison.OrdinalIgnoreCase) || string.Equals(reported, allowed, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Reads the tenant's settings and this month's use, in a short read of its own: nothing is held open across the model call (AIA-072).</summary>
public sealed record CheckAssistRun(string Feature) : IQuery<AssistAllowance>;

internal sealed class CheckAssistRunHandler(IAssistRepository assist, IOptions<AssistOptions> options, IBusinessDate businessDate) : IQueryHandler<CheckAssistRun, AssistAllowance>
{
    public async Task<Result<AssistAllowance>> Handle(CheckAssistRun query, CancellationToken cancellationToken)
    {
        if (await assist.Setting(cancellationToken) is not { ResidencyAllowsInference: true } setting)
        {
            return Error.Validation(AssistErrorCodes.ResidencyNotSet, "AI Assist needs the tenant's jurisdiction, and a legal basis if inference runs outside it (AIA-050).");
        }

        if (!options.Value.Supports(setting.InferenceRegion))
        {
            // A region set before the regions were the provider's (a country code): no call can run there, so none is made.
            return Error.Validation(AssistErrorCodes.RegionNotSupported,
                $"The tenant's inference region, {setting.InferenceRegion}, isn't one the AI provider can run in ({string.Join(", ", options.Value.InferenceRegions)}). Choose one in the settings.");
        }

        if (query.Feature is AssistFeatures.ExplainRefusal or AssistFeatures.ExplainPremium && !setting.ExplanationsEnabled)
        {
            return Error.Validation(AssistErrorCodes.NotEnabled, "The tenant hasn't switched explanations on.");
        }

        var month = new DateOnly(businessDate.Today.Year, businessDate.Today.Month, 1);
        var used = (await assist.Usage(businessDate.StartOf(month), businessDate.StartOf(month.AddMonths(1)), cancellationToken)).Tokens;
        return new AssistAllowance(used >= (setting.MonthlyTokenCap ?? options.Value.DefaultMonthlyTokenCap), setting.InferenceRegion);
    }
}

/// <summary>Records a finished run, its payload and, for an explanation, the explanation, in one short transaction after the call (AIA-072).</summary>
/// <param name="Audit">What the audit entry adds about the request, such as the record a refusal was about (AIA-039): never sent to a model.</param>
public sealed record RecordAssistRun(AssistRun? Run, AssistRunPayload? Payload, AssistExplanation? Explanation, IReadOnlyDictionary<string, string?>? Audit = null) : ICommand<Guid>;

internal sealed class RecordAssistRunHandler(IAssistRepository assist, IExecutionContext execution, IAuditTrail audit) : ICommandHandler<RecordAssistRun, Guid>
{
    public Task<Result<Guid>> Handle(RecordAssistRun command, CancellationToken cancellationToken)
    {
        if (command.Run is { } run)
        {
            assist.Add(run);
            assist.Add(command.Payload!);
            audit.Record(new AuditEntry("ASSIST", "Run", nameof(AssistRun), run.Id.ToString(), new Dictionary<string, string?>
            {
                ["feature"] = run.Feature, ["status"] = run.Status, ["actor"] = execution.ActorId,
            }));
        }

        if (command.Explanation is { } explanation)
        {
            assist.Add(explanation);
            var detail = new Dictionary<string, string?>
            {
                ["kind"] = explanation.Kind, ["source"] = explanation.SourceRef, ["outcome"] = explanation.Outcome, ["reused"] = (explanation.ReusedFrom is not null).ToString(),
                ["actor"] = execution.ActorId,
            };
            foreach (var (key, value) in command.Audit ?? new Dictionary<string, string?>())
            {
                detail[key] = value;
            }

            audit.Record(new AuditEntry("ASSIST", "Explain", nameof(AssistExplanation), explanation.Id.ToString(), detail));
        }

        return Task.FromResult(Result<Guid>.Success(command.Explanation?.Id ?? command.Run?.Id ?? Guid.Empty));
    }
}

/// <summary>
/// One model call, the only way to the provider, in three steps so that no transaction is open while the model answers (AIA-072, P8): a short read of the
/// settings and cap, then the minimiser, the call and the verifier with nothing held, then a short write of the record. The model's output only ever comes back
/// as text for the caller to show (AIA-060). A refused run makes no call and records none. The cap is checked before the call, so calls in flight together can
/// pass it by what they use.
/// </summary>
public sealed class AssistRunner(IDispatcher dispatcher, IAiModelProvider provider, IOptions<AssistOptions> options, IClock clock)
{
    /// <summary>The checks and the call, recording nothing: the caller records the run, with whatever it adds (an explanation).</summary>
    public async Task<Result<AssistCall>> Call(AssistRunRequest request, CancellationToken cancellationToken)
    {
        var allowance = await dispatcher.Query(new CheckAssistRun(request.Feature), cancellationToken);
        if (allowance.IsFailure)
        {
            return allowance.Error;
        }

        if (allowance.Value.CapReached)
        {
            return Error.Validation(AssistErrorCodes.CapReached, "The tenant's monthly AI Assist cap is reached.");
        }

        var started = clock.UtcNow;
        var model = options.Value.ModelVersion;
        var exchange = await AssistExchange.Send(provider, model, request.PromptVersion, request.Instructions, request.Facts, cancellationToken, allowance.Value.InferenceGeo);
        var response = exchange.Response;
        // AIA-050: the answer counts only if the provider says it ran where the tenant allows; otherwise its text isn't shown, and its tokens still count.
        var status = response is not null && !allowance.Value.Allows(response.InferenceRegion) ? AssistRunStatuses.ResidencyMismatch : exchange.Status;
        var cost = exchange.InputTokens + exchange.OutputTokens == 0 ? null : options.Value.CostOf(model, exchange.InputTokens, exchange.OutputTokens);
        var run = AssistRun.Finish(request.Feature, started, clock.UtcNow, model, request.PromptVersion, status, exchange.InputTokens, exchange.OutputTokens,
            request.CorrelationId, response?.ServedModel, response?.InferenceRegion, cost?.Amount, cost?.Currency);
        var payload = AssistRunPayload.For(run.Id, exchange.Sent.Text, response?.Text, JsonSerializer.Serialize(exchange.Sent.Masked));
        var rejected = exchange.Rejected;
        return new AssistCall(run, payload, new AssistRunOutcome(run.Id, status, status == AssistRunStatuses.Verified ? response!.Text : null, rejected));
    }

    /// <summary>A call, recorded on its own.</summary>
    public async Task<Result<AssistRunOutcome>> Run(AssistRunRequest request, CancellationToken cancellationToken)
    {
        var call = await Call(request, cancellationToken);
        if (call.IsFailure)
        {
            return call.Error;
        }

        var recorded = await dispatcher.Send(new RecordAssistRun(call.Value.Run, call.Value.Payload, null), cancellationToken);
        return recorded.IsFailure ? recorded.Error : call.Value.Outcome;
    }
}
