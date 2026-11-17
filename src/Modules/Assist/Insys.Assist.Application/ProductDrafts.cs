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
using Insys.Assist.Contracts;
using Insys.Assist.Domain;
using Insys.BuildingBlocks.Application;
using Insys.BuildingBlocks.Application.Ai;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;
using Insys.Documents.Contracts;
using Insys.ProductFactory.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Insys.Assist.Application;

public interface IProductDraftRuns
{
    void Add(ProductDraftRun run);

    void Add(ProductDraftDocument document);

    void Add(DraftItem item);

    Task<ProductDraftRun?> Run(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProductDraftDocument>> Documents(Guid runId, CancellationToken cancellationToken);

    Task<IReadOnlyList<DraftItem>> Items(Guid runId, CancellationToken cancellationToken);

    /// <summary>The tenant's runs still queued or running, oldest first.</summary>
    Task<IReadOnlyList<ProductDraftRun>> Open(int limit, CancellationToken cancellationToken);
}

/// <summary>A document for a run, and the uploader's confirmation that it holds no customer personal data (D-16): without it, the run is refused.</summary>
public sealed record DraftDocumentConfirmation(Guid DocumentId, bool ConfirmsNoPersonalData);

/// <summary>
/// AIA-010: queue AI-1 for a product with uploaded documents, already checked by <see cref="ProductDraftStarter"/>; the run is queued for the job that acts
/// for the user (AIA-017). It writes only Assist's own rows.
/// </summary>
/// <param name="Subject">The caller's token subject, from which the job resolves their permissions again at each step.</param>
public sealed record QueueProductDraft(string ProductCode, IReadOnlyList<DocumentFacts> Documents, string Subject) : ICommand<ProductDraftRunView>;

public sealed record GetProductDraft(Guid RunId) : IQuery<ProductDraftRunView>;

public sealed record ProductDraftDocumentView(Guid DocumentId, string? FileName, string Sha256, long SizeBytes, string ConfirmedBy, DateTimeOffset ConfirmedAt, string? Kind,
    IReadOnlyList<int> UnreadablePages, IReadOnlyList<IdentifierFound> Identifiers, string? RefusalCode)
{
    public static ProductDraftDocumentView From(ProductDraftDocument d) => new(d.DocumentId, d.FileName, d.Sha256, d.SizeBytes, d.ConfirmedBy, d.ConfirmedAt, d.Kind,
        d.UnreadablePages, d.Identifiers is null ? [] : JsonSerializer.Deserialize<List<IdentifierFound>>(d.Identifiers, ProductDraftJson.Options) ?? [], d.RefusalCode);
}

/// <param name="Value">The proposed value as JSON text; a rate table is its rows of cells.</param>
public sealed record DraftItemView(Guid Id, string Kind, string Field, string? Value, string Flag, Guid? CitationDocumentId, int? CitationPage, string? CitationQuote,
    string CheckResult, string? CheckReason, bool InDraft)
{
    public static DraftItemView From(DraftItem i) => new(i.Id, i.Kind, i.FieldPath, i.Value, i.Flag, i.CitationDocumentId, i.CitationPage, i.CitationQuote, i.CheckResult,
        i.CheckReason, i.InDraft);
}

public sealed record ProductDraftRunView(Guid Id, string ProductCode, string Status, string RequestedBy, DateTimeOffset RequestedAt, DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt, string? OutcomeCode, int? DraftVersion, IReadOnlyList<ProductDraftDocumentView> Documents, IReadOnlyList<DraftItemView> Items, long RowVersion)
{
    public static ProductDraftRunView From(ProductDraftRun r, IEnumerable<ProductDraftDocument> documents, IEnumerable<DraftItem> items) => new(r.Id, r.ProductCode, r.Status,
        r.RequestedBy, r.RequestedAt, r.StartedAt, r.FinishedAt, r.OutcomeCode, r.DraftVersion, documents.Select(ProductDraftDocumentView.From).ToList(),
        items.Select(DraftItemView.From).ToList(), r.RowVersion);
}

internal static class ProductDraftJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

/// <summary>
/// AI-1's start (AIA-010, D-16, ai-assist.md §5.5), checked before anything is queued, each check in its own module's query with no transaction of Assist's
/// open: one to ten documents, each confirmed to hold no customer personal data, readable by the user, cleared by the malware scan and at most 20 MB; a
/// product they can read; Product Factory's permissions (the Draft is written as them); and the tenant's settings and cap.
/// </summary>
public sealed class ProductDraftStarter(IDispatcher dispatcher, IDocumentReader documents, IProductDrafts products, IExecutionContext execution)
{
    public async Task<Result<ProductDraftRunView>> Start(string productCode, IReadOnlyList<DraftDocumentConfirmation> confirmations, string subject,
        CancellationToken cancellationToken)
    {
        if (execution.ActorType != ActorType.User || !Guid.TryParse(execution.ActorId, out _) || string.IsNullOrWhiteSpace(subject))
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "AI-1 runs for a signed-in user.");
        }

        if (confirmations.Count is 0 or > DraftDocumentKinds.MaxFiles || confirmations.Select(d => d.DocumentId).Distinct().Count() != confirmations.Count)
        {
            return Error.Validation(AssistErrorCodes.DocumentNotAllowed, $"A run reads 1 to {DraftDocumentKinds.MaxFiles} different documents (ai-assist.md §5.5).");
        }

        if (confirmations.Any(d => !d.ConfirmsNoPersonalData))
        {
            return Error.Validation(AssistErrorCodes.ConfirmationRequired, "Confirm for each document that it holds no customer personal data (D-16).");
        }

        // The Draft is written as the user (AIA-017), so they need its permissions now; the job checks them again when it starts and when it writes.
        if (!execution.HasPermission(ProductPermissions.ProductCreate) || !execution.HasPermission(ProductPermissions.ProductUpdate)
            || !execution.HasPermission(DocumentPermissions.DocumentRead))
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "AI-1 writes the Draft as you: it needs product.create, product.update and document.read.");
        }

        var allowance = await dispatcher.Query(new CheckAssistRun(AssistFeatures.ProductDraft), cancellationToken);
        if (allowance.IsFailure)
        {
            return allowance.Error;
        }

        if (allowance.Value.CapReached)
        {
            return Error.Validation(AssistErrorCodes.CapReached, "The tenant's monthly AI Assist cap is reached.");
        }

        if (await products.Describe(productCode, cancellationToken) is null)
        {
            return Error.NotFound(ErrorCodes.NotFound, $"Product {productCode} is not registered.");
        }

        var facts = new List<DocumentFacts>();
        foreach (var confirmation in confirmations)
        {
            var read = await documents.Facts(confirmation.DocumentId, cancellationToken);
            if (!read.Succeeded)
            {
                return new Error(read.ErrorCode!, read.Message ?? "The document can't be read.", read.ErrorCode == ErrorCodes.NotFound ? ErrorKind.NotFound : ErrorKind.Forbidden);
            }

            // §5.5: the size is refused before anything is read; the scan comes first (AIA-010), so a held upload can't start a run.
            if (read.Facts!.SizeBytes > DraftDocumentKinds.MaxBytes)
            {
                return Error.Validation(AssistErrorCodes.DocumentNotAllowed, "A document AI-1 reads is at most 20 MB (ai-assist.md §5.5).");
            }

            // D-16: a document whose own category says it holds personal data contradicts the confirmation, so it is refused whatever was confirmed.
            if (read.Facts.Classification is "PERSONAL" or "HIGHLY_SENSITIVE")
            {
                return Error.Validation(AssistErrorCodes.DocumentNotAllowed,
                    "A document classified as personal or highly sensitive isn't read by AI-1: file product documents under a category that holds no personal data.");
            }

            if (read.Facts.ScanStatus != "CLEAN")
            {
                return Error.Conflict("DOCUMENT_QUARANTINED", "An upload is held until the malware scan clears it; start the run once it has (AIA-010).");
            }

            facts.Add(read.Facts);
        }

        return await dispatcher.Send(new QueueProductDraft(productCode, facts, subject), cancellationToken);
    }
}

internal sealed class QueueProductDraftHandler(IProductDraftRuns runs, IExecutionContext execution, IClock clock, IAuditTrail audit)
    : ICommandHandler<QueueProductDraft, ProductDraftRunView>
{
    public Task<Result<ProductDraftRunView>> Handle(QueueProductDraft command, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var run = ProductDraftRun.Queue(command.ProductCode, execution.ActorId, command.Subject, now, execution.CorrelationId);
        runs.Add(run);
        var confirmed = command.Documents.Select(f => ProductDraftDocument.Confirmed(run.Id, f.DocumentId, f.FileName, f.Sha256, f.SizeBytes, execution.ActorId, now)).ToList();
        foreach (var document in confirmed)
        {
            runs.Add(document);
            // D-16: the confirmation names the person, the document (id, file name, content hash) and the date, and is audited.
            audit.Record(new AuditEntry("ASSIST", "ConfirmNoPersonalData", nameof(ProductDraftDocument), document.Id.ToString(), new Dictionary<string, string?>
            {
                ["run"] = run.Id.ToString(), ["document"] = document.DocumentId.ToString(), ["fileName"] = document.FileName, ["sha256"] = document.Sha256,
                ["confirmedBy"] = document.ConfirmedBy, ["confirmedAt"] = document.ConfirmedAt.ToString("O"),
            }));
        }

        audit.Record(new AuditEntry("ASSIST", "StartProductDraft", nameof(ProductDraftRun), run.Id.ToString(), new Dictionary<string, string?>
        {
            ["product"] = run.ProductCode, ["documents"] = confirmed.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }));
        return Task.FromResult(Result<ProductDraftRunView>.Success(ProductDraftRunView.From(run, confirmed, [])));
    }
}

/// <summary>A run is read by the user it acts for, or by the job: its documents' names and quotes are theirs (security review of 7.4). Others don't find it.</summary>
internal sealed class GetProductDraftHandler(IProductDraftRuns runs, IExecutionContext execution) : IQueryHandler<GetProductDraft, ProductDraftRunView>
{
    public async Task<Result<ProductDraftRunView>> Handle(GetProductDraft query, CancellationToken cancellationToken) =>
        await runs.Run(query.RunId, cancellationToken) is { } run && (execution.ActorType == ActorType.Service || run.RequestedBy == execution.ActorId)
            ? ProductDraftRunView.From(run, await runs.Documents(run.Id, cancellationToken), await runs.Items(run.Id, cancellationToken))
            : Error.NotFound(ErrorCodes.NotFound, "The run was not found.");
}

/// <summary>The job's own steps, as a service: list the open runs, claim one, record its end. Nothing here writes outside Assist.</summary>
public sealed record OpenProductDrafts(int Limit) : IQuery<IReadOnlyList<ProductDraftRun>>;

public sealed record ClaimProductDraft(Guid RunId) : ICommand<ProductDraftRun>;

/// <summary>What a document turned out to be when read: its kind and unreadable pages, or why it was refused, with the identifiers' kinds and pages.</summary>
public sealed record DraftDocumentOutcome(Guid DocumentId, string? Kind, IReadOnlyList<int> UnreadablePages, string? RefusalCode, IReadOnlyList<IdentifierFound>? Identifiers);

public sealed record FinishProductDraft(Guid RunId, string Status, string? OutcomeCode, int? DraftVersion, IReadOnlyList<DraftDocumentOutcome> Documents,
    IReadOnlyList<CheckedItem> Items, AssistRun? AssistRun, AssistRunPayload? Payload) : ICommand<ProductDraftRun>;

internal sealed class OpenProductDraftsHandler(IProductDraftRuns runs) : IQueryHandler<OpenProductDrafts, IReadOnlyList<ProductDraftRun>>
{
    public async Task<Result<IReadOnlyList<ProductDraftRun>>> Handle(OpenProductDrafts query, CancellationToken cancellationToken) =>
        Result<IReadOnlyList<ProductDraftRun>>.Success(await runs.Open(query.Limit, cancellationToken));
}

internal sealed class ClaimProductDraftHandler(IProductDraftRuns runs, IClock clock) : ICommandHandler<ClaimProductDraft, ProductDraftRun>
{
    public async Task<Result<ProductDraftRun>> Handle(ClaimProductDraft command, CancellationToken cancellationToken)
    {
        if (await runs.Run(command.RunId, cancellationToken) is not { } run)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The run was not found.");
        }

        var started = run.Start(clock.UtcNow);
        return started.IsFailure ? started.Error : run;
    }
}

internal sealed class FinishProductDraftHandler(IProductDraftRuns runs, IAssistRepository assist, IClock clock, IAuditTrail audit) : ICommandHandler<FinishProductDraft, ProductDraftRun>
{
    public async Task<Result<ProductDraftRun>> Handle(FinishProductDraft command, CancellationToken cancellationToken)
    {
        if (await runs.Run(command.RunId, cancellationToken) is not { } run)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The run was not found.");
        }

        var now = clock.UtcNow;
        var ended = command.Status == ProductDraftStatuses.Drafted
            ? run.Drafted(command.DraftVersion!.Value, command.AssistRun?.Id, now)
            : run.End(command.Status, command.OutcomeCode!, now, command.DraftVersion, command.AssistRun?.Id);
        if (ended.IsFailure)
        {
            return ended.Error;
        }

        var documents = (await runs.Documents(run.Id, cancellationToken)).ToDictionary(d => d.DocumentId);
        foreach (var outcome in command.Documents)
        {
            if (!documents.TryGetValue(outcome.DocumentId, out var document))
            {
                continue;
            }

            if (outcome.Kind is { } kind)
            {
                document.Read(kind, outcome.UnreadablePages);
            }

            if (outcome.RefusalCode is { } code)
            {
                document.Refuse(code, outcome.Identifiers is { Count: > 0 } found ? JsonSerializer.Serialize(found, ProductDraftJson.Options) : null);
            }
        }

        foreach (var item in command.Items)
        {
            runs.Add(item.Flag == DraftItemFlags.Missing
                ? DraftItem.Missing(run.Id, item.Field)
                : DraftItem.Proposed(run.Id, item.Kind, item.Field, item.Value, item.Flag, item.DocumentId, item.Page, item.Quote, item.Dropped, item.InDraft));
        }

        if (command.AssistRun is { } assistRun)
        {
            assist.Add(assistRun);
            assist.Add(command.Payload!);
        }

        // The run's end, on behalf of its user: the Draft itself is audited by Product Factory as theirs.
        audit.Record(new AuditEntry("ASSIST", "FinishProductDraft", nameof(ProductDraftRun), run.Id.ToString(), new Dictionary<string, string?>
        {
            ["status"] = run.Status, ["outcome"] = run.OutcomeCode, ["draftVersion"] = run.DraftVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["onBehalfOf"] = run.RequestedBy, ["items"] = command.Items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["dropped"] = command.Items.Count(i => i.Dropped is not null).ToString(System.Globalization.CultureInfo.InvariantCulture),
        }));
        return run;
    }
}

public sealed class ProductDraftOptions
{
    /// <summary>The job's period and how many runs it takes per tenant per pass.</summary>
    public TimeSpan JobInterval { get; set; } = TimeSpan.FromSeconds(15);

    public int BatchSize { get; set; } = 5;

    public bool JobEnabled { get; set; } = true;

    /// <summary>A run still RUNNING after this long was interrupted: it ends ASSIST_RUN_STALLED and isn't resumed.</summary>
    public TimeSpan StallAfter { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>A run still QUEUED after this long ends ASSIST_RUN_EXPIRED: its confirmations aren't relied on days later.</summary>
    public TimeSpan QueuedFor { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>
/// AI-1's job (AIA-017): for each queued run, acting for the user who asked, with their permissions resolved afresh when it starts and again when it writes.
/// <list type="number">
/// <item>Claim the run (as a service), so it runs once.</item>
/// <item>As the user: check they may still act, then read each document through Documents (scan, permission, audit as theirs), check its content is the one
/// confirmed and its type, extract it, and run the identifier check. Any refusal ends the run before anything is sent.</item>
/// <item>With no scope or transaction open: the minimiser, the model call (text in, text out, no tools: AIA-060) and INSYS's checks.</item>
/// <item>As the user again, permissions resolved again: create the Draft version and edit its own fields through Product Factory's commands.</item>
/// <item>Record the run's end, items and metering (as a service).</item>
/// </list>
/// </summary>
public sealed class ProductDraftWorker(IServiceScopeFactory scopes, ActingUserScopes acting, IAiModelProvider provider, IOptions<AssistOptions> assistOptions,
    IOptions<ProductDraftOptions> options, IDraftDocumentExtractor extractor, IClock clock, ILogger<ProductDraftWorker> logger)
{
    public const string ServiceName = "assist-product-draft";

    /// <summary>The purpose recorded on each sensitive document read (DOC-003).</summary>
    public const string ReadPurpose = "AI-1 product draft";

    /// <summary>Runs the tenant's open runs once; returns how many it finished.</summary>
    public async Task<int> RunTenant(Guid tenantId, CancellationToken cancellationToken)
    {
        IReadOnlyList<ProductDraftRun> open;
        await using (var scope = Service(tenantId))
        {
            var listed = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Query(new OpenProductDrafts(options.Value.BatchSize), cancellationToken);
            open = listed.IsFailure ? [] : listed.Value;
        }

        var finished = 0;
        foreach (var run in open)
        {
            if (run.Status == ProductDraftStatuses.Running)
            {
                // Interrupted part way (a restart): never resumed, since its Draft may exist; ended, so the user starts a new run.
                if (run.StartedAt is { } started && clock.UtcNow - started > options.Value.StallAfter)
                {
                    finished += await Finish(tenantId, run, ProductDraftStatuses.Failed, AssistErrorCodes.RunStalled, cancellationToken: cancellationToken) ? 1 : 0;
                }

                continue;
            }

            if (clock.UtcNow - run.RequestedAt > options.Value.QueuedFor)
            {
                finished += await Finish(tenantId, run, ProductDraftStatuses.Failed, AssistErrorCodes.RunExpired, cancellationToken: cancellationToken) ? 1 : 0;
                continue;
            }

            finished += await Process(tenantId, run, cancellationToken) ? 1 : 0;
        }

        return finished;
    }

    private async Task<bool> Process(Guid tenantId, ProductDraftRun queued, CancellationToken cancellationToken)
    {
        DateTimeOffset? claimedAt;
        await using (var scope = Service(tenantId))
        {
            var claimed = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new ClaimProductDraft(queued.Id), cancellationToken);
            if (claimed.IsFailure)
            {
                return false; // claimed by another instance, or a concurrent pass
            }

            claimedAt = claimed.Value.StartedAt;
        }

        var userId = Guid.Parse(queued.RequestedBy);
        List<DraftDocumentOutcome> outcomes = [];
        List<SentDocument> sent = [];
        ProductDraftTarget? target;

        // Step 2, as the user, at the start (AIA-017).
        await using (var user = await acting.Begin(tenantId, queued.RequestedSubject, userId, queued.CorrelationId, cancellationToken))
        {
            if (user is null || user.Refusal(AssistPermissions.ProductDraftRun, "ASSIST_PRODUCT_DRAFT_RUN") is not null)
            {
                return await Finish(tenantId, queued, ProductDraftStatuses.Failed, AssistErrorCodes.UserCannotAct, cancellationToken: cancellationToken);
            }

            target = await user.Services.GetRequiredService<IProductDrafts>().Describe(queued.ProductCode, cancellationToken);
            if (target is null)
            {
                return await Finish(tenantId, queued, ProductDraftStatuses.Failed, AssistErrorCodes.UserCannotAct, cancellationToken: cancellationToken);
            }

            var confirmed = await user.Services.GetRequiredService<IDispatcher>().Query(new GetProductDraft(queued.Id), cancellationToken);
            if (confirmed.IsFailure)
            {
                return await Finish(tenantId, queued, ProductDraftStatuses.Failed, confirmed.Error.Code, cancellationToken: cancellationToken);
            }

            var reader = user.Services.GetRequiredService<IDocumentReader>();
            var number = 0;
            foreach (var document in confirmed.Value.Documents)
            {
                number++;
                var read = await reader.Read(document.DocumentId, ReadPurpose, cancellationToken);
                if (!read.Succeeded)
                {
                    // A refusal the user would get reading it themselves (moved out of their reach, quarantined, disposed): nothing is sent.
                    outcomes.Add(new DraftDocumentOutcome(document.DocumentId, null, [], read.ErrorCode, null));
                    return await Finish(tenantId, queued, ProductDraftStatuses.Refused, read.ErrorCode!, outcomes, cancellationToken: cancellationToken);
                }

                var content = read.Content!;
                if (Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content)) != document.Sha256)
                {
                    outcomes.Add(new DraftDocumentOutcome(document.DocumentId, null, [], AssistErrorCodes.ConfirmationStale, null));
                    return await Finish(tenantId, queued, ProductDraftStatuses.Refused, AssistErrorCodes.ConfirmationStale, outcomes, cancellationToken: cancellationToken);
                }

                if (content.LongLength > DraftDocumentKinds.MaxBytes || DraftDocumentKinds.Detect(content) is not { } kind)
                {
                    outcomes.Add(new DraftDocumentOutcome(document.DocumentId, null, [], AssistErrorCodes.DocumentNotAllowed, null));
                    return await Finish(tenantId, queued, ProductDraftStatuses.Refused, AssistErrorCodes.DocumentNotAllowed, outcomes, cancellationToken: cancellationToken);
                }

                var extracted = extractor.Extract(kind, content);
                if (extracted is { OverLimit: true } || sent.Sum(d => d.Extracted.Characters) + (extracted?.Characters ?? 0) > ExtractionLimits.MaxRunCharacters)
                {
                    outcomes.Add(new DraftDocumentOutcome(document.DocumentId, kind, [], AssistErrorCodes.SourceTooLarge, null));
                    return await Finish(tenantId, queued, ProductDraftStatuses.Refused, AssistErrorCodes.SourceTooLarge, outcomes, cancellationToken: cancellationToken);
                }

                if (extracted is null || extracted.Pages.All(p => string.IsNullOrWhiteSpace(p.Text)))
                {
                    outcomes.Add(new DraftDocumentOutcome(document.DocumentId, kind, extracted?.UnreadablePages ?? [], AssistErrorCodes.SourceUnreadable, null));
                    return await Finish(tenantId, queued, ProductDraftStatuses.Refused, AssistErrorCodes.SourceUnreadable, outcomes, cancellationToken: cancellationToken);
                }

                // D-16 §5.5: an obvious identifier refuses the run, named by kind and page, never masked and sent.
                if (IdentifierCheck.Find(extracted) is { Count: > 0 } found)
                {
                    outcomes.Add(new DraftDocumentOutcome(document.DocumentId, kind, extracted.UnreadablePages, AssistErrorCodes.DocumentIdentifiers, found));
                    return await Finish(tenantId, queued, ProductDraftStatuses.Refused, AssistErrorCodes.DocumentIdentifiers, outcomes, cancellationToken: cancellationToken);
                }

                outcomes.Add(new DraftDocumentOutcome(document.DocumentId, kind, extracted.UnreadablePages, null, null));
                sent.Add(new SentDocument(number, document.DocumentId, document.FileName, Minimised(extracted)));
            }
        }

        // Step 3, holding nothing: the model call and INSYS's checks (AIA-072, P8).
        AssistAllowance allowance;
        DateOnly businessDay;
        await using (var scope = Service(tenantId))
        {
            businessDay = scope.ServiceProvider.GetRequiredService<IBusinessDate>().Today;
            var checkedRun = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Query(new CheckAssistRun(AssistFeatures.ProductDraft), cancellationToken);
            if (checkedRun.IsFailure || checkedRun.Value.CapReached)
            {
                return await Finish(tenantId, queued, ProductDraftStatuses.Failed, checkedRun.IsFailure ? checkedRun.Error.Code : AssistErrorCodes.CapReached, outcomes,
                    cancellationToken: cancellationToken);
            }

            allowance = checkedRun.Value;
        }

        var template = assistOptions.Value.PromptFor(AssistFeatures.ProductDraft);
        if (template is null)
        {
            return await Finish(tenantId, queued, ProductDraftStatuses.Failed, AssistErrorCodes.Unavailable, outcomes, cancellationToken: cancellationToken);
        }

        var fields = DraftFields.For(target);
        var payload = PromptData.Delimit(DraftPayload.Render(target, fields, sent));
        var model = assistOptions.Value.ModelVersion;
        var startedCall = clock.UtcNow;
        AiModelResponse? response = null;
        try
        {
            response = await provider.Complete(new AiModelRequest(model, template.Version, template.Text, payload, allowance.InferenceGeo), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("AI-1 run {RunId}: the model was unavailable ({Error})", queued.Id, ex.GetType().Name);
        }

        var residencyOk = response is not null && allowance.Allows(response.InferenceRegion);
        var items = residencyOk ? DraftProposalCheck.Check(response!.Text, fields, sent, businessDay) : null;
        var status = response is null ? AssistRunStatuses.Unavailable : !residencyOk ? AssistRunStatuses.ResidencyMismatch
            : items is null ? AssistRunStatuses.Rejected : AssistRunStatuses.Verified;
        var cost = response is null ? null : assistOptions.Value.CostOf(model, response.InputTokens, response.OutputTokens);
        var assistRun = AssistRun.Finish(AssistFeatures.ProductDraft, startedCall, clock.UtcNow, model, template.Version, status, response?.InputTokens ?? 0,
            response?.OutputTokens ?? 0, queued.CorrelationId, response?.ServedModel, response?.InferenceRegion, cost?.Amount, cost?.Currency);
        var record = AssistRunPayload.For(assistRun.Id, payload, residencyOk ? response!.Text : null, "{}");
        if (items is null)
        {
            var code = response is null ? AssistErrorCodes.Unavailable : !residencyOk ? AssistErrorCodes.ResidencyMismatch : AssistErrorCodes.OutputRejected;
            return await Finish(tenantId, queued, ProductDraftStatuses.Failed, code, outcomes, [], null, assistRun, record, cancellationToken);
        }

        // Step 4, as the user again, permissions resolved again (AIA-017): the Draft through Product Factory's own commands.
        var draft = Fields(items);
        ProductDraftOutcome written;
        await using (var user = await acting.Begin(tenantId, queued.RequestedSubject, userId, queued.CorrelationId, cancellationToken))
        {
            if (user is null || user.Refusal(AssistPermissions.ProductDraftRun, "ASSIST_PRODUCT_DRAFT_RUN") is not null)
            {
                return await Finish(tenantId, queued, ProductDraftStatuses.Failed, AssistErrorCodes.UserCannotAct, outcomes, NotWritten(items), null, assistRun, record,
                    cancellationToken);
            }

            // Still this pass's run: not ended meanwhile as stalled or expired by another instance, so it writes once (security review of 7.4).
            var current = await user.Services.GetRequiredService<IDispatcher>().Query(new GetProductDraft(queued.Id), cancellationToken);
            if (current.IsFailure || current.Value.Status != ProductDraftStatuses.Running || !SameInstant(current.Value.StartedAt, claimedAt))
            {
                return false;
            }

            var today = user.Services.GetRequiredService<IBusinessDate>().Today;
            written = await user.Services.GetRequiredService<IProductDrafts>().Create(queued.ProductCode, draft.EffectiveFrom ?? today, draft.Fields, cancellationToken);
        }

        if (!written.Succeeded)
        {
            return await Finish(tenantId, queued, ProductDraftStatuses.Failed, written.ErrorCode!, outcomes, NotWritten(items), written.Version,
                assistRun, record, cancellationToken);
        }

        return await Finish(tenantId, queued, ProductDraftStatuses.Drafted, null, outcomes, items, written.Version, assistRun, record, cancellationToken);
    }

    /// <summary>The version's own fields from the items kept for the Draft (OQ-184 (a)).</summary>
    private static (DateOnly? EffectiveFrom, ProductDraftFields Fields) Fields(IReadOnlyList<CheckedItem> items)
    {
        string? Scalar(string field) => items.FirstOrDefault(i => i.InDraft && i.Field == field)?.Value is { } json ? JsonSerializer.Deserialize<string>(json) : null;
        var coverages = items.FirstOrDefault(i => i.InDraft && i.Field == DraftFields.Coverages)?.Value is { } list ? JsonSerializer.Deserialize<List<string>>(list) : null;
        var effective = Scalar(DraftFields.EffectiveFrom) is { } date ? DateOnly.ParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : (DateOnly?)null;
        return (effective, new ProductDraftFields(Scalar(DraftFields.Name)?.Trim(), coverages, Scalar(DraftFields.CoverStartsOn), Scalar(DraftFields.ReinstatementArrears)));
    }

    /// <summary>The same claim: PostgreSQL keeps microseconds, a clock ticks in tenths of them.</summary>
    private static bool SameInstant(DateTimeOffset? a, DateTimeOffset? b) => a is { } x && b is { } y && Math.Abs((x - y).Ticks) < 10;

    /// <summary>The items as recorded when no Draft was written: none of them is in one.</summary>
    private static IReadOnlyList<CheckedItem> NotWritten(IReadOnlyList<CheckedItem> items) => items.Select(i => i with { InDraft = false }).ToList();

    /// <summary>The minimiser on every page and cell (AIA-051), the backstop behind the identifier check; citations are checked against this text.</summary>
    private static ExtractedDocument Minimised(ExtractedDocument document) => document with
    {
        Pages = document.Pages.Select(p => p with { Text = Minimiser.Minimise(p.Text).Text }).ToList(),
        Tables = document.Tables.Select(t => t with { Rows = t.Rows.Select(r => (IReadOnlyList<string>)r.Select(c => Minimiser.Minimise(c).Text).ToList()).ToList() }).ToList(),
    };

    private async Task<bool> Finish(Guid tenantId, ProductDraftRun run, string status, string? code, IReadOnlyList<DraftDocumentOutcome>? documents = null,
        IReadOnlyList<CheckedItem>? items = null, int? version = null, AssistRun? assistRun = null, AssistRunPayload? payload = null,
        CancellationToken cancellationToken = default)
    {
        await using var scope = Service(tenantId);
        var finished = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new FinishProductDraft(run.Id, status, code, version, documents ?? [], items ?? [],
            assistRun, payload), cancellationToken);
        if (finished.IsFailure)
        {
            logger.LogError("AI-1 run {RunId} of tenant {TenantId}: recording its end failed with {Code}", run.Id, tenantId, finished.Error.Code);
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
