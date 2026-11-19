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
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;
using Insys.Documents.Contracts;

namespace Insys.Assist.Application;

/// <summary>The setup run's draft from its packs, with each drafted value's pack row (for its tailor marks, OQ-187 (a)).</summary>
public sealed record SetupDraft(IReadOnlyList<Guid> PackIds, IReadOnlyList<DraftValue> Values, IReadOnlyDictionary<(Guid Pack, int Line), StarterPackValue> Rows)
{
    /// <summary>The pack row a drafted value came from, when it came from one.</summary>
    public StarterPackValue? RowOf(DraftValue value) =>
        value.SourcePackId is { } pack && value.SourceLineNo is { } line && Rows.TryGetValue((pack, line), out var row) ? row : null;

    /// <summary>
    /// The rows AI may tailor now (AIA-082, OQ-187 (a), D-20): a Pack value, not regulated, whose pack row carries both explicit marks and names its columns and
    /// answers, with the values those answers hold. A value from the draft's packs, never from a past pass, so a pass always starts from the packs.
    /// </summary>
    public IReadOnlyList<TailorableRow> Tailorable(SetupAnswers answers) =>
    [
        .. Values.Where(v => v.Flag == ItemFlags.Pack && !v.Regulated && v.Values is not null)
            .Select(v => (Value: v, Row: RowOf(v)))
            .Where(x => x.Row is { MayBeTailored: true })
            .Select(x => new TailorableRow(x.Value.Table, x.Value.Code, x.Value.Values!, x.Row!.TailorColumns!,
                x.Row.TailorFrom!.ToDictionary(f => f, f => SetupAnswerFields.Values(answers, f), StringComparer.Ordinal))),
    ];
}

/// <summary>The draft assembled from the packs (AIA-082, AIA-090): the pack-only path, which AI-3's pass tailors and maps beside.</summary>
public sealed class SetupDrafting(IStarterPacks packs, ITenantContext tenant)
{
    public async Task<SetupDraft> Draft(SetupAnswers answers, CancellationToken cancellationToken)
    {
        var current = tenant.RequireTenantId();
        var chosen = SetupAssembly.Choose(await packs.List(null, cancellationToken), answers, current);
        var layers = new List<AssemblyLayer>();
        foreach (var (pack, fill) in chosen)
        {
            layers.Add(new AssemblyLayer(pack, await packs.Rows(pack.Id, cancellationToken), SetupAssembly.ApprovedFor(pack, current), fill));
        }

        var rows = layers.SelectMany(l => l.Rows).GroupBy(r => (r.PackId, r.LineNo)).ToDictionary(g => g.Key, g => g.First());
        return new SetupDraft([.. chosen.Select(c => c.Pack.Id)], SetupAssembly.Assemble(layers), rows);
    }
}

/// <summary>A document for a setup run, and its uploader's confirmation that it holds no customer personal data (D-16).</summary>
public sealed record SetupDocumentConfirmation(Guid DocumentId, bool ConfirmsNoPersonalData);

public sealed record SetupDocumentView(Guid DocumentId, string? FileName, string Sha256, long SizeBytes, string ConfirmedBy, DateTimeOffset ConfirmedAt, string? Kind,
    IReadOnlyList<int> UnreadablePages, IReadOnlyList<IdentifierFound> Identifiers, string? RefusalCode)
{
    public static SetupDocumentView From(SetupDocument d) => new(d.DocumentId, d.FileName, d.Sha256, d.SizeBytes, d.ConfirmedBy, d.ConfirmedAt, d.Kind, d.UnreadablePages,
        d.Identifiers is null ? [] : JsonSerializer.Deserialize<List<IdentifierFound>>(d.Identifiers, SetupAiJson.Options) ?? [], d.RefusalCode);
}

/// <summary>A setup run's documents, as now: those taken off it aren't listed. RowVersion is the run's, which an attach or a removal moves.</summary>
/// <param name="Id">The setup's id.</param>
public sealed record SetupDocumentsView(Guid Id, IReadOnlyList<SetupDocumentView> Documents, long RowVersion);

public sealed record SetupUnmappedView(Guid Id, Guid? DocumentId, int? Page, string? Quote, string? Table, string Reason)
{
    public static SetupUnmappedView From(SetupUnmapped u) => new(u.Id, u.DocumentId, u.Page, u.Quote, u.TargetTable, u.Reason);
}

/// <param name="Unmapped">What the pass couldn't map, and what INSYS's checks dropped, with why (AIA-083).</param>
public sealed record SetupAiPassView(Guid Id, Guid SetupId, int AnswersVersion, string Status, string RequestedBy, DateTimeOffset RequestedAt, DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt, string? OutcomeCode, int? Tailored, int? Mapped, IReadOnlyList<SetupUnmappedView> Unmapped, long RowVersion)
{
    public static SetupAiPassView From(SetupAiRun r, IEnumerable<SetupUnmapped> unmapped) => new(r.Id, r.SetupRunId, r.AnswersVersion, r.Status, r.RequestedBy, r.RequestedAt,
        r.StartedAt, r.FinishedAt, r.OutcomeCode, r.Tailored, r.Mapped, [.. unmapped.Where(u => u.AiRunId == r.Id).Select(SetupUnmappedView.From)], r.RowVersion);
}

public sealed record SetupAiPassesView(Guid SetupId, IReadOnlyList<SetupAiPassView> Passes);

internal static class SetupAiJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

public sealed record AttachSetupDocuments(Guid Id, IReadOnlyList<DocumentFacts> Documents, long ExpectedVersion) : ICommand<SetupDocumentsView>;

public sealed record RemoveSetupDocument(Guid Id, Guid DocumentId, long ExpectedVersion) : ICommand<SetupDocumentsView>;

public sealed record GetSetupDocuments(Guid Id) : IQuery<SetupDocumentsView>;

/// <param name="Subject">The caller's token subject, from which the job resolves their permissions again at each step (AIA-017).</param>
public sealed record QueueSetupAiPass(Guid Id, long ExpectedVersion, string Subject) : ICommand<SetupAiPassView>;

/// <summary>
/// An AI pass's start (AIA-080, AIA-090), checked before anything is queued, each in its own module's query with no transaction of Assist's open: a signed-in user,
/// the tenant's AI_ASSIST entitlement (the pack-only path needs none), AI Assist's settings and the monthly cap. The command then checks the setup itself.
/// </summary>
public sealed class SetupAiStarter(IDispatcher dispatcher, IExecutionContext execution, ITenantContext tenant, IBusinessDate businessDate,
    Tenancy.Contracts.ITenantFunctionSettings settings)
{
    public async Task<Result<SetupAiPassView>> Queue(Guid id, long expectedVersion, string subject, CancellationToken cancellationToken)
    {
        if (execution.ActorType != ActorType.User || !Guid.TryParse(execution.ActorId, out _) || string.IsNullOrWhiteSpace(subject))
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "AI-3's pass runs for a signed-in user.");
        }

        var entitled = await settings.For(tenant.RequireTenantId(), businessDate.Today, cancellationToken);
        if (!entitled.EntitledFeatures.Contains(AssistFeatures.Entitlement))
        {
            return Error.Forbidden(ErrorCodes.FeatureNotEntitled, "AI-3's tailoring and mapping need the tenant's AI_ASSIST entitlement; the pack-only draft doesn't (AIA-090).");
        }

        var allowance = await dispatcher.Query(new CheckAssistRun(AssistFeatures.TenantSetup), cancellationToken);
        if (allowance.IsFailure)
        {
            return allowance.Error;
        }

        if (allowance.Value.CapReached)
        {
            return Error.Validation(AssistErrorCodes.CapReached, "The tenant's monthly AI Assist cap is reached.");
        }

        return await dispatcher.Send(new QueueSetupAiPass(id, expectedVersion, subject), cancellationToken);
    }
}

public sealed record GetSetupAiPasses(Guid Id) : IQuery<SetupAiPassesView>;

/// <summary>
/// D-16 for AI-3's documents (ai-assist.md §5.5, "AI-3's tenant documents (7.8)"), checked before anything is recorded, each in its own module's query: one to ten
/// documents, each confirmed to hold no customer personal data by its own uploader, readable by them, cleared by the malware scan, at most 20 MB, and not filed
/// as personal or highly sensitive.
/// </summary>
public sealed class SetupDocumentStarter(IDispatcher dispatcher, IDocumentReader documents, IExecutionContext execution)
{
    public async Task<Result<SetupDocumentsView>> Attach(Guid id, IReadOnlyList<SetupDocumentConfirmation> confirmations, long expectedVersion,
        CancellationToken cancellationToken)
    {
        if (execution.ActorType != ActorType.User || !Guid.TryParse(execution.ActorId, out _))
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "AI-3's documents are confirmed by a signed-in user.");
        }

        if (confirmations.Count is 0 or > DraftDocumentKinds.MaxFiles || confirmations.Select(d => d.DocumentId).Distinct().Count() != confirmations.Count)
        {
            return Error.Validation(AssistErrorCodes.DocumentNotAllowed, $"A setup reads 1 to {DraftDocumentKinds.MaxFiles} different documents (ai-assist.md §5.5).");
        }

        if (confirmations.Any(d => !d.ConfirmsNoPersonalData))
        {
            return Error.Validation(AssistErrorCodes.ConfirmationRequired, "Confirm for each document that it holds no customer personal data (D-16).");
        }

        if (!execution.HasPermission(DocumentPermissions.DocumentRead))
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "AI-3 reads the documents as you: it needs document.read.");
        }

        var facts = new List<DocumentFacts>();
        foreach (var confirmation in confirmations)
        {
            var read = await documents.Facts(confirmation.DocumentId, cancellationToken);
            if (!read.Succeeded)
            {
                return new Error(read.ErrorCode!, read.Message ?? "The document can't be read.", read.ErrorCode == ErrorCodes.NotFound ? ErrorKind.NotFound : ErrorKind.Forbidden);
            }

            if (read.Facts!.SizeBytes > DraftDocumentKinds.MaxBytes)
            {
                return Error.Validation(AssistErrorCodes.DocumentNotAllowed, "A document AI-3 reads is at most 20 MB (ai-assist.md §5.5).");
            }

            if (read.Facts.UploadedBy != execution.ActorId)
            {
                return Error.Forbidden(AssistErrorCodes.NotUploader,
                    "Only the user who uploaded a document can confirm it for AI-3. Upload the document again yourself to use it.");
            }

            if (read.Facts.Classification is "PERSONAL" or "HIGHLY_SENSITIVE")
            {
                return Error.Validation(AssistErrorCodes.DocumentNotAllowed,
                    "A document classified as personal or highly sensitive isn't read by AI-3: file setup documents under a category that holds no personal data.");
            }

            if (read.Facts.ScanStatus != "CLEAN")
            {
                return Error.Conflict("DOCUMENT_QUARANTINED", "An upload is held until the malware scan clears it; attach it once it has (AIA-010).");
            }

            facts.Add(read.Facts);
        }

        return await dispatcher.Send(new AttachSetupDocuments(id, facts, expectedVersion), cancellationToken);
    }
}

/// <summary>
/// AI-3's documents and AI passes on a setup run (AI Assist SRS §5A.3, AIA-083, AIA-090): only on a draft, only while the tenant is onboarding (AIA-086), and a
/// pass only with the tenant's AI_ASSIST entitlement and AI Assist's settings (AIA-080). The pack-only draft is untouched by any of it until a pass's values are
/// recorded, and each of those is undecided until a person accepts it.
/// </summary>
internal sealed class SetupAiHandlers(ISetupRuns runs, IExecutionContext execution, ITenantContext tenant, IClock clock, IAuditTrail audit,
    Tenancy.Contracts.ITenantStatusQuery tenants)
    : ICommandHandler<AttachSetupDocuments, SetupDocumentsView>, ICommandHandler<RemoveSetupDocument, SetupDocumentsView>, IQueryHandler<GetSetupDocuments, SetupDocumentsView>,
        ICommandHandler<QueueSetupAiPass, SetupAiPassView>, IQueryHandler<GetSetupAiPasses, SetupAiPassesView>
{
    public async Task<Result<SetupDocumentsView>> Handle(AttachSetupDocuments command, CancellationToken cancellationToken)
    {
        var draft = await Draft(command.Id, command.ExpectedVersion, cancellationToken);
        if (draft.IsFailure)
        {
            return draft.Error;
        }

        var run = draft.Value;
        var documents = await runs.Documents(run.Id, cancellationToken);
        var byDocument = documents.ToDictionary(d => d.DocumentId);
        var now = clock.UtcNow;
        var attached = new List<SetupDocument>();
        foreach (var facts in command.Documents)
        {
            if (byDocument.TryGetValue(facts.DocumentId, out var existing))
            {
                existing.Reconfirm(facts.FileName, facts.Sha256, facts.SizeBytes, execution.ActorId, now);
                attached.Add(existing);
            }
            else
            {
                var added = SetupDocument.Confirmed(run.Id, facts.DocumentId, facts.FileName, facts.Sha256, facts.SizeBytes, execution.ActorId, now);
                runs.Add(added);
                attached.Add(added);
                byDocument[added.DocumentId] = added;
            }
        }

        if (byDocument.Values.Count(d => !d.Removed) > DraftDocumentKinds.MaxFiles)
        {
            return Error.Validation(AssistErrorCodes.DocumentNotAllowed, $"A setup reads at most {DraftDocumentKinds.MaxFiles} documents (ai-assist.md §5.5): remove one first.");
        }

        foreach (var document in attached)
        {
            // D-16: the confirmation names the person, the document (id, file name, content hash) and the date, and is audited (IAM-006).
            audit.Record(new AuditEntry("ASSIST", "ConfirmNoPersonalData", nameof(SetupDocument), document.Id.ToString(), new Dictionary<string, string?>
            {
                ["setup"] = run.Id.ToString(), ["document"] = document.DocumentId.ToString(), ["fileName"] = document.FileName, ["sha256"] = document.Sha256,
                ["confirmedBy"] = document.ConfirmedBy, ["confirmedAt"] = document.ConfirmedAt.ToString("O"),
            }));
        }

        run.ValueDecided();
        return View(run, byDocument.Values);
    }

    public async Task<Result<SetupDocumentsView>> Handle(RemoveSetupDocument command, CancellationToken cancellationToken)
    {
        var draft = await Draft(command.Id, command.ExpectedVersion, cancellationToken);
        if (draft.IsFailure)
        {
            return draft.Error;
        }

        var run = draft.Value;
        var documents = await runs.Documents(run.Id, cancellationToken);
        if (documents.FirstOrDefault(d => d.DocumentId == command.DocumentId && !d.Removed) is not { } document)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The document isn't on this setup.");
        }

        document.Remove();
        run.ValueDecided();
        audit.Record(new AuditEntry("ASSIST", "RemoveSetupDocument", nameof(SetupDocument), document.Id.ToString(), new Dictionary<string, string?>
        {
            ["setup"] = run.Id.ToString(), ["document"] = document.DocumentId.ToString(),
        }));
        return View(run, documents);
    }

    public async Task<Result<SetupDocumentsView>> Handle(GetSetupDocuments query, CancellationToken cancellationToken) =>
        await runs.Run(query.Id, cancellationToken) is { } run
            ? View(run, await runs.Documents(run.Id, cancellationToken))
            : Error.NotFound(ErrorCodes.NotFound, "The setup was not found.");

    public async Task<Result<SetupAiPassView>> Handle(QueueSetupAiPass command, CancellationToken cancellationToken)
    {
        if (execution.ActorType != ActorType.User || !Guid.TryParse(execution.ActorId, out _) || string.IsNullOrWhiteSpace(command.Subject))
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "AI-3's pass runs for a signed-in user.");
        }

        var draft = await Draft(command.Id, command.ExpectedVersion, cancellationToken);
        if (draft.IsFailure)
        {
            return draft.Error;
        }

        var run = draft.Value;
        if ((await runs.AiRuns(run.Id, cancellationToken)).Any(p => p.Open))
        {
            return Error.Conflict(AssistErrorCodes.SetupPassOpen, "A pass is already queued or running on this setup; wait for it to finish.");
        }

        var pass = SetupAiRun.Queue(run.Id, run.AnswersVersion, execution.ActorId, command.Subject, clock.UtcNow, execution.CorrelationId);
        runs.Add(pass);
        run.ValueDecided();
        audit.Record(new AuditEntry("ASSIST", "QueueSetupAiPass", nameof(SetupAiRun), pass.Id.ToString(), new Dictionary<string, string?>
        {
            ["setup"] = run.Id.ToString(), ["answersVersion"] = run.AnswersVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }));
        return SetupAiPassView.From(pass, []);
    }

    public async Task<Result<SetupAiPassesView>> Handle(GetSetupAiPasses query, CancellationToken cancellationToken)
    {
        if (await runs.Run(query.Id, cancellationToken) is not { } run)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The setup was not found.");
        }

        var unmapped = await runs.Unmapped(run.Id, cancellationToken);
        return new SetupAiPassesView(run.Id, [.. (await runs.AiRuns(run.Id, cancellationToken)).Select(p => SetupAiPassView.From(p, unmapped))]);
    }

    /// <summary>A draft setup at the version the caller read, while the tenant is onboarding (AIA-086).</summary>
    private async Task<Result<SetupRun>> Draft(Guid id, long expectedVersion, CancellationToken cancellationToken)
    {
        if (await tenants.UserAccess(tenant.RequireTenantId(), cancellationToken) != Tenancy.Contracts.TenantUserAccess.ConfigurationOnly)
        {
            return Error.Conflict(PackErrorCodes.TenantLive, "The setup wizard runs only while the tenant is onboarding.");
        }

        if (await runs.Run(id, cancellationToken) is not { } run)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The setup was not found.");
        }

        if (run.Status != SetupStatuses.Draft)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "Only a draft setup takes documents or an AI pass.");
        }

        return run.RowVersion != expectedVersion
            ? Error.PreconditionFailed(ErrorCodes.PreconditionFailed, "The setup has changed since it was read. Read it again.")
            : run;
    }

    private static SetupDocumentsView View(SetupRun run, IEnumerable<SetupDocument> documents) =>
        new(run.Id, [.. documents.Where(d => !d.Removed).OrderBy(d => d.Id).Select(SetupDocumentView.From)], run.RowVersion + (run.HasChanges ? 1 : 0));
}

/// <summary>The job's own steps, as a service: list the open passes, claim one, read its input as the user, record its end.</summary>
public sealed record OpenSetupAiPasses(int Limit) : IQuery<IReadOnlyList<SetupAiRun>>;

public sealed record ClaimSetupAiPass(Guid PassId) : ICommand<SetupAiRun>;

/// <summary>What a pass reads, as the user it acts for: the run's answers and the rows it may tailor, the codes already in the draft, and its documents.</summary>
public sealed record SetupAiInput(Guid SetupId, int AnswersVersion, IReadOnlyList<TailorableRow> Tailorable, IReadOnlySet<(string Table, string Code)> PackCodes,
    IReadOnlyList<SetupDocument> Documents);

public sealed record GetSetupAiInput(Guid PassId) : IQuery<SetupAiInput>;

/// <summary>What a document turned out to be when read: its kind and unreadable pages, or why it was refused, with the identifiers' kinds and pages.</summary>
public sealed record SetupDocumentOutcome(Guid DocumentId, string? Kind, IReadOnlyList<int> UnreadablePages, string? RefusalCode, IReadOnlyList<IdentifierFound>? Identifiers);

public sealed record FinishSetupAiPass(Guid PassId, string Status, string? OutcomeCode, IReadOnlyList<SetupDocumentOutcome> Documents, SetupAiResult? Result,
    AssistRun? AssistRun, AssistRunPayload? Payload) : ICommand<SetupAiRun>;

internal sealed class SetupAiJobHandlers(ISetupRuns runs, SetupDrafting drafting, IAssistRepository assist, IClock clock, IAuditTrail audit)
    : IQueryHandler<OpenSetupAiPasses, IReadOnlyList<SetupAiRun>>, ICommandHandler<ClaimSetupAiPass, SetupAiRun>, IQueryHandler<GetSetupAiInput, SetupAiInput>,
        ICommandHandler<FinishSetupAiPass, SetupAiRun>
{
    public async Task<Result<IReadOnlyList<SetupAiRun>>> Handle(OpenSetupAiPasses query, CancellationToken cancellationToken) =>
        Result<IReadOnlyList<SetupAiRun>>.Success(await runs.OpenAiRuns(query.Limit, cancellationToken));

    public async Task<Result<SetupAiRun>> Handle(ClaimSetupAiPass command, CancellationToken cancellationToken)
    {
        if (await runs.AiRun(command.PassId, cancellationToken) is not { } pass)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The pass was not found.");
        }

        var started = pass.Start(clock.UtcNow);
        return started.IsFailure ? started.Error : pass;
    }

    public async Task<Result<SetupAiInput>> Handle(GetSetupAiInput query, CancellationToken cancellationToken)
    {
        if (await runs.AiRun(query.PassId, cancellationToken) is not { } pass || await runs.Run(pass.SetupRunId, cancellationToken) is not { } run)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The pass was not found.");
        }

        if (run.Status != SetupStatuses.Draft || run.AnswersVersion != pass.AnswersVersion)
        {
            return Error.Conflict(AssistErrorCodes.SetupChanged, "The setup changed after the pass was asked for: it was submitted, or its answers changed.");
        }

        if (await runs.Answers(run.Id, run.AnswersVersion, cancellationToken) is not { } answers)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The setup's answers were not found.");
        }

        var given = JsonSerializer.Deserialize<SetupAnswers>(answers.Answers, SetupAiJson.Options)!;
        var draft = await drafting.Draft(given, cancellationToken);
        var items = await runs.Items(run.Id, cancellationToken);
        var inDraft = items.Where(i => i.InDraft).Select(i => (i.TableName, i.RowCode)).ToHashSet();
        var tailorable = draft.Tailorable(given).Where(r => inDraft.Contains((r.Table, r.Code))).ToList();
        // Mapped rows go beside the pack's, never over them (OQ-188): any row of the draft not itself mapped holds its code.
        var codes = items.Where(i => i.Flag != ItemFlags.Mapped).Select(i => (i.TableName, i.RowCode)).ToHashSet();
        var documents = (await runs.Documents(run.Id, cancellationToken)).Where(d => !d.Removed).OrderBy(d => d.Id).ToList();
        return new SetupAiInput(run.Id, run.AnswersVersion, tailorable, codes, documents);
    }

    public async Task<Result<SetupAiRun>> Handle(FinishSetupAiPass command, CancellationToken cancellationToken)
    {
        if (await runs.AiRun(command.PassId, cancellationToken) is not { } pass || await runs.Run(pass.SetupRunId, cancellationToken) is not { } run)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The pass was not found.");
        }

        var now = clock.UtcNow;
        var documents = (await runs.Documents(run.Id, cancellationToken)).ToDictionary(d => d.DocumentId);
        foreach (var outcome in command.Documents)
        {
            if (documents.TryGetValue(outcome.DocumentId, out var document))
            {
                document.Read(outcome.Kind, outcome.UnreadablePages, outcome.RefusalCode,
                    outcome.Identifiers is { Count: > 0 } found ? JsonSerializer.Serialize(found, SetupAiJson.Options) : null);
            }
        }

        if (command.AssistRun is { } assistRun)
        {
            assist.Add(assistRun);
            assist.Add(command.Payload!);
        }

        if (command.Status != SetupAiStatuses.Drafted || command.Result is not { } result)
        {
            var ended = pass.End(command.Status == SetupAiStatuses.Drafted ? SetupAiStatuses.Failed : command.Status, command.OutcomeCode ?? AssistErrorCodes.OutputRejected, now,
                command.AssistRun?.Id);
            Audit(pass, run);
            return ended.IsFailure ? ended.Error : pass;
        }

        // A pass whose setup moved on meanwhile records nothing in it: its answers, or its status, are no longer the ones it read.
        if (run.Status != SetupStatuses.Draft || run.AnswersVersion != pass.AnswersVersion
            || await runs.Answers(run.Id, run.AnswersVersion, cancellationToken) is not { } answers)
        {
            var ended = pass.End(SetupAiStatuses.Failed, AssistErrorCodes.SetupChanged, now, command.AssistRun?.Id);
            Audit(pass, run);
            return ended.IsFailure ? ended.Error : pass;
        }

        var items = await runs.Items(run.Id, cancellationToken);
        var byRow = items.ToDictionary(i => (i.TableName, i.RowCode));
        var given = JsonSerializer.Deserialize<SetupAnswers>(answers.Answers, SetupAiJson.Options)!;
        var pack = (await drafting.Draft(given, cancellationToken)).Values.ToDictionary(v => (v.Table, v.Code));
        var tailored = result.Tailored.ToDictionary(t => (t.Table, t.Code));
        var mapped = result.Mapped.ToDictionary(m => (m.Table, m.Code));
        var tailoredCount = 0;
        var mappedCount = 0;

        foreach (var item in items.Where(i => i.InDraft))
        {
            if (tailored.TryGetValue((item.TableName, item.RowCode), out var t))
            {
                tailoredCount += item.Tailor(pass.Id, t.Values, t.Answer, t.AnswerValue).IsSuccess ? 1 : 0;
            }
            else if (item.Flag == ItemFlags.Tailored && pack.TryGetValue((item.TableName, item.RowCode), out var original))
            {
                // A new pass replaces the last one's results: a value it didn't tailor again is the pack's again, undecided.
                item.Redraft(run.AnswersVersion, original.Class, original.Regulated, original.Flag, original.Values, original.SourcePackId, original.SourceLineNo,
                    original.MissingReason);
            }
            else if (item.Flag == ItemFlags.Mapped && !mapped.ContainsKey((item.TableName, item.RowCode)))
            {
                item.LeaveDraft(pass.RequestedBy, now);
            }
        }

        foreach (var m in result.Mapped)
        {
            if (byRow.TryGetValue((m.Table, m.Code), out var existing))
            {
                // Only a mapped row is mapped again; any other row of the draft holds its code (OQ-188), which the check already dropped.
                mappedCount += existing.Remap(run.AnswersVersion, pass.Id, m.Values, m.DocumentId, m.Page, m.Quote).IsSuccess ? 1 : 0;
            }
            else
            {
                runs.Add(SetupItem.Mapped(run.Id, run.AnswersVersion, pass.Id, m.Table, m.Code, m.Class, m.Values, m.DocumentId, m.Page, m.Quote));
                mappedCount++;
            }
        }

        foreach (var u in result.Unmapped)
        {
            runs.Add(SetupUnmapped.For(run.Id, pass.Id, u.DocumentId, u.Page, u.Quote, u.Table, u.Reason));
        }

        var drafted = pass.Drafted(tailoredCount, mappedCount, result.Unmapped.Count, command.AssistRun?.Id, now);
        if (drafted.IsFailure)
        {
            return drafted.Error;
        }

        run.ValueDecided();
        Audit(pass, run);
        return pass;
    }

    private void Audit(SetupAiRun pass, SetupRun run) =>
        audit.Record(new AuditEntry("ASSIST", "FinishSetupAiPass", nameof(SetupAiRun), pass.Id.ToString(), new Dictionary<string, string?>
        {
            ["setup"] = run.Id.ToString(), ["status"] = pass.Status, ["outcome"] = pass.OutcomeCode, ["onBehalfOf"] = pass.RequestedBy,
            ["tailored"] = pass.Tailored?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["mapped"] = pass.Mapped?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["unmapped"] = pass.Unmapped?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }));
}
