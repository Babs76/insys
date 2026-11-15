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
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Application.Messaging;
using Microsoft.Extensions.Options;

namespace Insys.Assist.Application;

public sealed class AssistOptions
{
    /// <summary>Configuration, not code (AIA-074). "stub" in batch 7.1, as no real model exists yet.</summary>
    public string ModelVersion { get; set; } = "stub";

    /// <summary>The cap for a tenant the operator hasn't set one for: low until batch 7.3's measured cost (addendum §6).</summary>
    public long DefaultMonthlyTokenCap { get; set; } = 100_000;
}

/// <param name="Instructions">The prompt template's text (<see cref="PromptTemplates"/>): INSYS's own words, never record data (AIA-061).</param>
/// <param name="Facts">The structured facts, the only data sent (D-15), and what the verifier compares numbers against.</param>
public sealed record AssistRunRequest(string Feature, string PromptVersion, string Instructions, AllowedFacts Facts, Guid? CorrelationId = null);

/// <param name="Text">The verified output, or null: the caller then shows the plain data (AIA-033), or carries on without (P8).</param>
public sealed record AssistRunOutcome(Guid RunId, string Status, string? Text, IReadOnlyList<string> RejectedNumbers);

public sealed record AssistUsage(long Tokens, long Runs, long Rejected, long Unavailable);

/// <summary>A model call made and checked, not yet recorded: its run, its payload and how it ended.</summary>
public sealed record AssistCall(AssistRun Run, AssistRunPayload Payload, AssistRunOutcome Outcome);

/// <summary>Whether a run may be made: the settings allow it, and whether the cap is reached (checked only before a call, so a reused explanation still shows).</summary>
public sealed record AssistAllowance(bool CapReached);

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

        if (query.Feature is AssistFeatures.ExplainRefusal or AssistFeatures.ExplainPremium && !setting.ExplanationsEnabled)
        {
            return Error.Validation(AssistErrorCodes.NotEnabled, "The tenant hasn't switched explanations on.");
        }

        var month = new DateOnly(businessDate.Today.Year, businessDate.Today.Month, 1);
        var used = (await assist.Usage(businessDate.StartOf(month), businessDate.StartOf(month.AddMonths(1)), cancellationToken)).Tokens;
        return new AssistAllowance(used >= (setting.MonthlyTokenCap ?? options.Value.DefaultMonthlyTokenCap));
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
        // A second layer behind the allowed fields (addendum §5.4): the minimiser masks identifiers a structured field could still carry.
        var minimised = Minimiser.Minimise(request.Facts.Render());
        var model = options.Value.ModelVersion;
        AiModelResponse? response = null;
        try
        {
            response = await provider.Complete(new AiModelRequest(model, request.PromptVersion, request.Instructions, PromptData.Delimit(minimised.Text)), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Recorded below: INSYS never waits on the AI (P8), whatever the provider threw.
        }

        // Against the facts' values only, every amount with its currency, so a stated currency must match (addendum §5.1).
        var rejected = response is null ? [] : NumberVerifier.Verify(response.Text, Minimiser.Minimise(request.Facts.Values()).Text, strictCurrency: true).Rejected;
        var status = response is null ? AssistRunStatuses.Unavailable : rejected.Count > 0 ? AssistRunStatuses.Rejected : AssistRunStatuses.Verified;
        var run = AssistRun.Finish(request.Feature, started, clock.UtcNow, model, request.PromptVersion, status, response?.InputTokens ?? 0, response?.OutputTokens ?? 0, request.CorrelationId);
        var payload = AssistRunPayload.For(run.Id, minimised.Text, response?.Text, JsonSerializer.Serialize(minimised.Masked));
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
