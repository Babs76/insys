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
using Microsoft.Extensions.Options;

namespace Insys.Assist.Application;

public sealed class AssistOptions
{
    /// <summary>Configuration, not code (AIA-074). "stub" in batch 7.1, as no real model exists yet.</summary>
    public string ModelVersion { get; set; } = "stub";

    /// <summary>The cap for a tenant the operator hasn't set one for: low until batch 7.3's measured cost (addendum §6).</summary>
    public long DefaultMonthlyTokenCap { get; set; } = 100_000;
}

/// <param name="SourceData">INSYS's own recorded figures: what the verifier compares numbers against.</param>
/// <param name="UntrustedText">Free text anyone could have typed (a note, a reason, a document's words). It's sent as data, but a number in it doesn't count as a source, or an injected figure would pass the verifier (AIA-061).</param>
/// <param name="Known">The record's names and numbers, by kind, for the minimiser.</param>
public sealed record AssistRunRequest(string Feature, string PromptVersion, string Instructions, string SourceData, IReadOnlyDictionary<string, IReadOnlyCollection<string>>? Known = null,
    Guid? CorrelationId = null, string? UntrustedText = null);

/// <param name="Text">The verified output, or null: the caller then shows the plain data (AIA-033), or carries on without (P8).</param>
public sealed record AssistRunOutcome(Guid RunId, string Status, string? Text, IReadOnlyList<string> RejectedNumbers);

public sealed record AssistUsage(long Tokens, long Runs, long Rejected, long Unavailable);

/// <summary>A run in one transaction, for batch 7.2's explanations. It holds a transaction open across the model call, which 7.2 should weigh against AIA-072.</summary>
public sealed record RunAssist(AssistRunRequest Request) : BuildingBlocks.Application.Messaging.ICommand<AssistRunOutcome>;

internal sealed class RunAssistHandler(AssistRunner runner) : BuildingBlocks.Application.Messaging.ICommandHandler<RunAssist, AssistRunOutcome>
{
    public Task<Result<AssistRunOutcome>> Handle(RunAssist command, CancellationToken cancellationToken) => runner.Run(command.Request, cancellationToken);
}

/// <summary>
/// One model call, the only way to the provider: settings and cap first, then the minimiser, the call, the verifier and the record. The model's output only ever comes back as text
/// for the caller to show (AIA-060). A refused run makes no call and records none. The cap is checked before the call, so calls in flight together can pass it by what they use.
/// </summary>
public sealed class AssistRunner(IAssistRepository assist, IAiModelProvider provider, IOptions<AssistOptions> options, IClock clock, IBusinessDate businessDate, IExecutionContext execution,
    IAuditTrail audit)
{
    public async Task<Result<AssistRunOutcome>> Run(AssistRunRequest request, CancellationToken cancellationToken)
    {
        if (await assist.Setting(cancellationToken) is not { ResidencyAllowsInference: true } setting)
        {
            return Error.Validation(AssistErrorCodes.ResidencyNotSet, "AI Assist needs the tenant's jurisdiction, and a legal basis if inference runs outside it (AIA-050).");
        }

        var today = businessDate.Today;
        var used = (await assist.Usage(businessDate.StartOf(new DateOnly(today.Year, today.Month, 1)), businessDate.StartOf(new DateOnly(today.Year, today.Month, 1).AddMonths(1)), cancellationToken)).Tokens;
        if (used >= (setting.MonthlyTokenCap ?? options.Value.DefaultMonthlyTokenCap))
        {
            return Error.Validation(AssistErrorCodes.CapReached, "The tenant's monthly AI Assist cap is reached.");
        }

        var started = clock.UtcNow;
        var minimised = Minimiser.Minimise(request.SourceData, request.Known);
        var untrusted = Minimiser.Minimise(request.UntrustedText ?? "", request.Known);
        var sent = untrusted.Text.Length == 0 ? minimised.Text : $"{minimised.Text}\n{untrusted.Text}";
        var model = options.Value.ModelVersion;
        AiModelResponse? response = null;
        try
        {
            response = await provider.Complete(new AiModelRequest(model, request.PromptVersion, request.Instructions, PromptData.Delimit(sent)), cancellationToken);
        }
        catch (AiModelUnavailableException)
        {
            // Recorded below: INSYS never waits on the AI (P8).
        }

        var rejected = response is null ? [] : NumberVerifier.Verify(response.Text, minimised.Text).Rejected;
        var status = response is null ? AssistRunStatuses.Unavailable : rejected.Count > 0 ? AssistRunStatuses.Rejected : AssistRunStatuses.Verified;
        var run = AssistRun.Finish(request.Feature, started, clock.UtcNow, model, request.PromptVersion, status, response?.InputTokens ?? 0, response?.OutputTokens ?? 0, request.CorrelationId);
        assist.Add(run);
        assist.Add(AssistRunPayload.For(run.Id, sent, response?.Text,
            JsonSerializer.Serialize(minimised.Masked.Concat(untrusted.Masked).GroupBy(m => m.Key).ToDictionary(g => g.Key, g => g.Sum(m => m.Value)))));
        audit.Record(new AuditEntry("ASSIST", "Run", nameof(AssistRun), run.Id.ToString(), new Dictionary<string, string?>
        {
            ["feature"] = request.Feature, ["status"] = status, ["actor"] = execution.ActorId,
        }));
        return new AssistRunOutcome(run.Id, status, status == AssistRunStatuses.Verified ? response!.Text : null, rejected);
    }
}
