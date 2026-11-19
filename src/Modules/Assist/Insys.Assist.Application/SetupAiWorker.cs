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
using Insys.Assist.Contracts;
using Insys.Assist.Domain;
using Insys.BuildingBlocks.Application;
using Insys.BuildingBlocks.Application.Ai;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.Documents.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Insys.Assist.Application;

/// <summary>AI-3's job settings: as AI-1's (Assist:TenantSetups).</summary>
public sealed class SetupAiOptions
{
    public TimeSpan JobInterval { get; set; } = TimeSpan.FromSeconds(15);

    public int BatchSize { get; set; } = 5;

    public bool JobEnabled { get; set; } = true;

    /// <summary>A pass still RUNNING after this long was interrupted: it ends ASSIST_RUN_STALLED and isn't resumed.</summary>
    public TimeSpan StallAfter { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>A pass still QUEUED after this long ends ASSIST_RUN_EXPIRED: its documents' confirmations aren't relied on days later.</summary>
    public TimeSpan QueuedFor { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>
/// AI-3's job (AI Assist SRS §8.3, step 3; AIA-017 as for AI-1): for each queued pass, acting for the user who asked, with their permissions resolved afresh.
/// <list type="number">
/// <item>Claim the pass (as a service), so it runs once.</item>
/// <item>As the user: check they may still act; read the setup's answers, tailorable rows and codes; read each document through Documents (scan, permission,
/// audit as theirs), check its content is the one confirmed and its type, extract it, and run the identifier check. Any refusal ends the pass before anything is sent.</item>
/// <item>With no scope or transaction open: the minimiser, the model call and INSYS's checks (<see cref="SetupAiCheck"/>).</item>
/// <item>Record the pass's end, its values (undecided), what it couldn't map, and the metering (as a service). Nothing takes effect until a person accepts each
/// value and a second person approves the SETUP change set.</item>
/// </list>
/// </summary>
public sealed class SetupAiWorker(IServiceScopeFactory scopes, ActingUserScopes acting, IAiModelProvider provider, IOptions<AssistOptions> assistOptions,
    IOptions<SetupAiOptions> options, IDraftDocumentExtractor extractor, IClock clock, ILogger<SetupAiWorker> logger)
{
    public const string ServiceName = "assist-tenant-setup";

    /// <summary>The purpose recorded on each sensitive document read (DOC-003).</summary>
    public const string ReadPurpose = "AI-3 tenant setup";

    public async Task<int> RunTenant(Guid tenantId, CancellationToken cancellationToken)
    {
        IReadOnlyList<SetupAiRun> open;
        await using (var scope = Service(tenantId))
        {
            var listed = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Query(new OpenSetupAiPasses(options.Value.BatchSize), cancellationToken);
            open = listed.IsFailure ? [] : listed.Value;
        }

        var finished = 0;
        foreach (var pass in open)
        {
            if (pass.Status == SetupAiStatuses.Running)
            {
                if (pass.StartedAt is { } started && clock.UtcNow - started > options.Value.StallAfter)
                {
                    finished += await Finish(tenantId, pass, SetupAiStatuses.Failed, AssistErrorCodes.RunStalled, cancellationToken: cancellationToken) ? 1 : 0;
                }

                continue;
            }

            if (clock.UtcNow - pass.RequestedAt > options.Value.QueuedFor)
            {
                finished += await Finish(tenantId, pass, SetupAiStatuses.Failed, AssistErrorCodes.RunExpired, cancellationToken: cancellationToken) ? 1 : 0;
                continue;
            }

            finished += await Process(tenantId, pass, cancellationToken) ? 1 : 0;
        }

        return finished;
    }

    private async Task<bool> Process(Guid tenantId, SetupAiRun queued, CancellationToken cancellationToken)
    {
        await using (var scope = Service(tenantId))
        {
            if ((await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new ClaimSetupAiPass(queued.Id), cancellationToken)).IsFailure)
            {
                return false;
            }
        }

        var userId = Guid.Parse(queued.RequestedBy);
        List<SetupDocumentOutcome> outcomes = [];
        List<SentDocument> sent = [];
        SetupAiInput input;
        IReadOnlyList<SetupMappingTarget> targets;

        await using (var user = await acting.Begin(tenantId, queued.RequestedSubject, userId, queued.CorrelationId, cancellationToken))
        {
            if (user is null || user.Refusal(AssistPermissions.TenantSetupRun, "ASSIST_TENANT_SETUP_RUN") is not null)
            {
                return await Finish(tenantId, queued, SetupAiStatuses.Failed, AssistErrorCodes.UserCannotAct, cancellationToken: cancellationToken);
            }

            var read = await user.Services.GetRequiredService<IDispatcher>().Query(new GetSetupAiInput(queued.Id), cancellationToken);
            if (read.IsFailure)
            {
                return await Finish(tenantId, queued, SetupAiStatuses.Failed, read.Error.Code, cancellationToken: cancellationToken);
            }

            input = read.Value;
            targets = SetupMappingTargets.For([.. user.Services.GetRequiredService<Identity.Contracts.IRoleTemplateCatalogue>().Templates.Select(t => t.Code)]);
            var reader = user.Services.GetRequiredService<IDocumentReader>();
            var number = 0;
            foreach (var document in input.Documents)
            {
                number++;
                var refusal = await ReadOne(reader, document, number, outcomes, sent, cancellationToken);
                if (refusal is not null)
                {
                    return await Finish(tenantId, queued, SetupAiStatuses.Refused, refusal, outcomes, cancellationToken: cancellationToken);
                }
            }
        }

        // With nothing held open (AIA-072, P8): the call and INSYS's checks.
        AssistAllowance allowance;
        await using (var scope = Service(tenantId))
        {
            var checkedRun = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Query(new CheckAssistRun(AssistFeatures.TenantSetup), cancellationToken);
            if (checkedRun.IsFailure || checkedRun.Value.CapReached)
            {
                return await Finish(tenantId, queued, SetupAiStatuses.Failed, checkedRun.IsFailure ? checkedRun.Error.Code : AssistErrorCodes.CapReached, outcomes,
                    cancellationToken: cancellationToken);
            }

            allowance = checkedRun.Value;
        }

        var template = assistOptions.Value.PromptFor(AssistFeatures.TenantSetup);
        if (template is null)
        {
            return await Finish(tenantId, queued, SetupAiStatuses.Failed, AssistErrorCodes.Unavailable, outcomes, cancellationToken: cancellationToken);
        }

        var payload = PromptData.Delimit(SetupAiPayload.Render(input.Tailorable, targets, sent));
        var model = assistOptions.Value.ModelVersion;
        var startedCall = clock.UtcNow;
        AiModelResponse? response = null;
        try
        {
            response = await provider.Complete(new AiModelRequest(model, template.Version, template.Text, payload, allowance.InferenceGeo), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("AI-3 pass {PassId}: the model was unavailable ({Error})", queued.Id, ex.GetType().Name);
        }

        var residencyOk = response is not null && allowance.Allows(response.InferenceRegion);
        var result = residencyOk ? SetupAiCheck.Check(response!.Text, input.Tailorable, targets, sent, input.PackCodes) : null;
        var status = response is null ? AssistRunStatuses.Unavailable : !residencyOk ? AssistRunStatuses.ResidencyMismatch
            : result is null ? AssistRunStatuses.Rejected : AssistRunStatuses.Verified;
        var cost = response is null ? null : assistOptions.Value.CostOf(model, response.InputTokens, response.OutputTokens);
        var assistRun = AssistRun.Finish(AssistFeatures.TenantSetup, startedCall, clock.UtcNow, model, template.Version, status, response?.InputTokens ?? 0,
            response?.OutputTokens ?? 0, queued.CorrelationId, response?.ServedModel, response?.InferenceRegion, cost?.Amount, cost?.Currency);
        var record = AssistRunPayload.For(assistRun.Id, payload, residencyOk ? response!.Text : null, "{}");
        if (result is null)
        {
            var code = response is null ? AssistErrorCodes.Unavailable : !residencyOk ? AssistErrorCodes.ResidencyMismatch : AssistErrorCodes.OutputRejected;
            return await Finish(tenantId, queued, SetupAiStatuses.Failed, code, outcomes, null, assistRun, record, cancellationToken);
        }

        return await Finish(tenantId, queued, SetupAiStatuses.Drafted, null, outcomes, result, assistRun, record, cancellationToken);
    }

    /// <summary>One document read as the user and checked (ai-assist.md §5.5), or the code it was refused with; nothing of a refused document is sent.</summary>
    private async Task<string?> ReadOne(IDocumentReader reader, SetupDocument document, int number, List<SetupDocumentOutcome> outcomes, List<SentDocument> sent,
        CancellationToken cancellationToken)
    {
        var read = await reader.Read(document.DocumentId, ReadPurpose, cancellationToken);
        string? Refuse(string code, string? kind = null, IReadOnlyList<int>? pages = null, IReadOnlyList<IdentifierFound>? found = null)
        {
            outcomes.Add(new SetupDocumentOutcome(document.DocumentId, kind, pages ?? [], code, found));
            return code;
        }

        if (!read.Succeeded)
        {
            return Refuse(read.ErrorCode!);
        }

        var content = read.Content!;
        if (Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content)) != document.Sha256)
        {
            return Refuse(AssistErrorCodes.ConfirmationStale);
        }

        if (content.LongLength > DraftDocumentKinds.MaxBytes || DraftDocumentKinds.Detect(content) is not { } kind)
        {
            return Refuse(AssistErrorCodes.DocumentNotAllowed);
        }

        var extracted = extractor.Extract(kind, content);
        if (extracted is { OverLimit: true } || sent.Sum(d => d.Extracted.Characters) + (extracted?.Characters ?? 0) > ExtractionLimits.MaxRunCharacters)
        {
            return Refuse(AssistErrorCodes.SourceTooLarge, kind);
        }

        if (extracted is null || extracted.Pages.All(p => string.IsNullOrWhiteSpace(p.Text)))
        {
            return Refuse(AssistErrorCodes.SourceUnreadable, kind, extracted?.UnreadablePages);
        }

        // D-16 §5.5: an obvious identifier refuses the pass, named by kind and page, never masked and sent.
        if (IdentifierCheck.Find(extracted) is { Count: > 0 } identifiers)
        {
            return Refuse(AssistErrorCodes.DocumentIdentifiers, kind, extracted.UnreadablePages, identifiers);
        }

        outcomes.Add(new SetupDocumentOutcome(document.DocumentId, kind, extracted.UnreadablePages, null, null));
        sent.Add(new SentDocument(number, document.DocumentId, document.FileName, Minimised(extracted)));
        return null;
    }

    /// <summary>The minimiser on every page and cell (AIA-051), the backstop behind the identifier check; citations are checked against this text.</summary>
    private static ExtractedDocument Minimised(ExtractedDocument document) => document with
    {
        Pages = document.Pages.Select(p => p with { Text = Minimiser.Minimise(p.Text).Text }).ToList(),
        Tables = document.Tables.Select(t => t with { Rows = t.Rows.Select(r => (IReadOnlyList<string>)r.Select(c => Minimiser.Minimise(c).Text).ToList()).ToList() }).ToList(),
    };

    private async Task<bool> Finish(Guid tenantId, SetupAiRun pass, string status, string? code, IReadOnlyList<SetupDocumentOutcome>? documents = null,
        SetupAiResult? result = null, AssistRun? assistRun = null, AssistRunPayload? payload = null, CancellationToken cancellationToken = default)
    {
        await using var scope = Service(tenantId);
        var finished = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new FinishSetupAiPass(pass.Id, status, code, documents ?? [], result, assistRun, payload),
            cancellationToken);
        if (finished.IsFailure)
        {
            logger.LogError("AI-3 pass {PassId} of tenant {TenantId}: recording its end failed with {Code}", pass.Id, tenantId, finished.Error.Code);
        }

        return finished.IsSuccess;
    }

    private AsyncServiceScope Service(Guid tenantId)
    {
        var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ExecutionScope>().InitialiseForService(tenantId, ServiceName);
        return scope;
    }
}
