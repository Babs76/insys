/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : StaticData
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
using FluentValidation;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Contracts.StaticData;
using Insys.BuildingBlocks.Domain;
using Insys.StaticData.Contracts;
using Insys.StaticData.Domain.Changes;
using Insys.Workflow.Contracts;

namespace Insys.StaticData.Application.Changes;

public interface IChangeRequestRepository
{
    Task<StaticDataChange?> Get(Guid id, CancellationToken cancellationToken);

    Task<StaticDataChange?> FindPending(string entityCode, string recordKey, CancellationToken cancellationToken);

    void Add(StaticDataChange change);

    void Add(ImportBatch batch);

    Task<bool> BatchExists(Guid id, CancellationToken cancellationToken);

    /// <summary>The tenant's settings, or null before anyone has changed them (defaults apply).</summary>
    Task<TenantGovernanceSettings?> Settings(CancellationToken cancellationToken);

    void Add(TenantGovernanceSettings settings);

    Task<SetupChangeSet?> SetupSet(Guid id, CancellationToken cancellationToken);

    void Add(SetupChangeSet set);

    /// <summary>The change requests submitted to a SETUP change set, one per batch.</summary>
    Task<IReadOnlyList<StaticDataChange>> SetupBatches(Guid setId, CancellationToken cancellationToken);
}

/// <param name="PendingTier2MakerChecker">False while a switch-off waits for a second person's approval; otherwise null.</param>
public sealed record GovernanceSettingsView(bool Tier2MakerChecker, bool Tier1MakerChecker, bool? PendingTier2MakerChecker, Guid? PendingRequestedBy, long RowVersion);

/// <summary>Reads the tenant's static data governance settings (defaults when never changed).</summary>
public sealed record GetGovernanceSettings : IQuery<GovernanceSettingsView>;

/// <summary>
/// Switches Tier 2 maker-checker on (at once) or off (after a second person approves it in Workflow) for the tenant
/// (SDM-003, OQ-046). Every request is audited.
/// </summary>
public sealed record ChangeTier2MakerChecker(bool Enabled, long ExpectedVersion) : ICommand<long>, IVersionedCommand;

internal sealed class GetGovernanceSettingsHandler(IChangeRequestRepository repository) : IQueryHandler<GetGovernanceSettings, GovernanceSettingsView>
{
    public async Task<Result<GovernanceSettingsView>> Handle(GetGovernanceSettings query, CancellationToken cancellationToken) =>
        await repository.Settings(cancellationToken) is { } settings
            ? new GovernanceSettingsView(settings.Tier2MakerChecker, true, settings.PendingTier2MakerChecker, settings.PendingRequestedBy, settings.RowVersion)
            : new GovernanceSettingsView(true, true, null, null, 0);
}

internal sealed class ChangeTier2MakerCheckerHandler(IChangeRequestRepository repository, IExecutionContext execution, IClock clock, IAuditTrail audit)
    : ICommandHandler<ChangeTier2MakerChecker, long>
{
    public async Task<Result<long>> Handle(ChangeTier2MakerChecker command, CancellationToken cancellationToken)
    {
        var actor = Checker.Require(execution);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        var settings = await repository.Settings(cancellationToken);
        if ((settings?.RowVersion ?? 0) != command.ExpectedVersion)
        {
            return Error.PreconditionFailed(ErrorCodes.PreconditionFailed, "The settings have changed since they were read.");
        }

        if (settings is null)
        {
            settings = TenantGovernanceSettings.Defaults(Guid.CreateVersion7());
            repository.Add(settings);
        }

        var before = settings.Tier2MakerChecker;
        bool changed;
        if (command.Enabled)
        {
            changed = settings.SwitchTier2On();
        }
        else
        {
            var requested = settings.RequestTier2Off(actor.Value, StaticDataApprovals.ApprovalType(1), clock.UtcNow);
            if (requested.IsFailure)
            {
                return requested.Error;
            }

            changed = requested.Value;
        }

        if (!changed && settings.RowVersion > 0)
        {
            return settings.RowVersion;
        }

        audit.Record(new AuditEntry("STATIC_DATA_GOVERNANCE", command.Enabled ? "ChangeTier2MakerChecker" : "RequestTier2MakerCheckerOff",
            nameof(TenantGovernanceSettings), settings.Id.ToString(),
            new Dictionary<string, string?> { ["before"] = before.ToString(), ["after"] = command.Enabled.ToString(), ["pending"] = (!command.Enabled).ToString() }));
        return settings.RowVersion;
    }
}

/// <summary>A static data change request (SDM-003), with the way to the version it creates or changes (BR-015).</summary>
/// <param name="VersionNumber">The version the change creates or changes.</param>
/// <param name="EntityRoute">
/// The table's API route under /api/v1, for example "billing/static/billing-frequencies", ending "/global" for a platform-global
/// row. The version is GET {entityRoute}/{record.code}/versions (its versionNumber among them), and its lifecycle actions are
/// under {entityRoute}/{record.code}/versions/{versionNumber}. Null only when the record reference does not name it.
/// </param>
public sealed record ChangeRequestView(
    Guid Id, string RequestNumber, string EntityCode, string RecordKey, string Action, ChangeStatus Status, Guid MakerId, Guid? CheckerId, DateOnly EffectiveFrom,
    string Reason, JsonElement Record, int VersionNumber, string? EntityRoute, long RowVersion, LatestRejection? LatestRejection = null);

/// <summary>
/// Where a change request's version lives (BR-015): from the record reference the owning module sent (StaticRecordRef,
/// stored in change_request.after), and the record key "{code}@{version}" for the version number.
/// </summary>
public static class ChangeRequestLinks
{
    public static int VersionNumber(string recordKey, JsonElement record) =>
        record.ValueKind == JsonValueKind.Object && record.TryGetProperty("version", out var version) && version.TryGetInt32(out var number)
            ? number
            : int.TryParse(recordKey.AsSpan(recordKey.LastIndexOf('@') + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
                out var fromKey) ? fromKey : 0;

    public static string? EntityRoute(string recordKey, JsonElement record)
    {
        if (record.ValueKind != JsonValueKind.Object
            || !record.TryGetProperty("moduleRoute", out var module) || module.GetString() is not { Length: > 0 } moduleRoute
            || !record.TryGetProperty("entityRoute", out var entity) || entity.GetString() is not { Length: > 0 } entityRoute)
        {
            return null;
        }

        var global = record.TryGetProperty("global", out var g) ? g.ValueKind == JsonValueKind.True : recordKey.StartsWith("G:", StringComparison.Ordinal);
        return $"{moduleRoute}/static/{entityRoute}{(global ? "/global" : "")}";
    }
}

public sealed record ImportBatchView(Guid Id, string BatchNumber, string EntityCode, string FileRef, int RowCount, int RejectedCount, ImportStatus Status,
    Guid SubmittedBy, IReadOnlyList<ImportLineError> Errors);

public interface IChangeRequestQueries
{
    Task<IReadOnlyList<ChangeRequestView>> List(string? status, string? entityCode, Guid? afterId, int limit, CancellationToken cancellationToken);

    Task<ChangeRequestView?> Get(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<ImportBatchView>> Imports(Guid? afterId, int limit, CancellationToken cancellationToken);
}

public sealed record ListChangeRequests(string? Status, string? EntityCode, Guid? AfterId, int Limit) : IQuery<IReadOnlyList<ChangeRequestView>>;

public sealed record GetChangeRequest(Guid Id) : IQuery<ChangeRequestView>;

public sealed record ListImportBatches(Guid? AfterId, int Limit) : IQuery<IReadOnlyList<ImportBatchView>>;

internal static class Checker
{
    public static Result<Guid> Require(IExecutionContext execution) =>
        execution.ActorType == ActorType.User && Guid.TryParse(execution.ActorId, out var id)
            ? id
            : Error.Forbidden(ErrorCodes.Forbidden, "Governance settings are changed by identified users.");
}

internal sealed class ListChangeRequestsHandler(IChangeRequestQueries queries) : IQueryHandler<ListChangeRequests, IReadOnlyList<ChangeRequestView>>
{
    public async Task<Result<IReadOnlyList<ChangeRequestView>>> Handle(ListChangeRequests query, CancellationToken cancellationToken) =>
        Result.Success(await queries.List(query.Status, query.EntityCode, query.AfterId, query.Limit, cancellationToken));
}

internal sealed class GetChangeRequestHandler(IChangeRequestQueries queries, IApprovalHistory history) : IQueryHandler<GetChangeRequest, ChangeRequestView>
{
    public async Task<Result<ChangeRequestView>> Handle(GetChangeRequest query, CancellationToken cancellationToken) =>
        await queries.Get(query.Id, cancellationToken) is { } view
            ? view with { LatestRejection = await history.Latest(StaticDataApprovals.BusinessRefType, view.Id, cancellationToken) }
            : Error.NotFound(ErrorCodes.NotFound, "The change request was not found.");
}

internal sealed class ListImportBatchesHandler(IChangeRequestQueries queries) : IQueryHandler<ListImportBatches, IReadOnlyList<ImportBatchView>>
{
    public async Task<Result<IReadOnlyList<ImportBatchView>>> Handle(ListImportBatches query, CancellationToken cancellationToken) =>
        Result.Success(await queries.Imports(query.AfterId, query.Limit, cancellationToken));
}

/// <summary>
/// The Static Data module's policies for the owning modules' events. Each runs behind the inbox, so a duplicate
/// delivery changes nothing (INT-005); a change request in an unexpected state is left as it is, except that an approval's
/// ending (rejected, expired, withdrawn) it would not take parks the delivery (D-5, ADR-022).
/// </summary>
public sealed class StaticDataGovernancePolicies(IChangeRequestRepository changes, IClock clock, Tenancy.Contracts.ITenantStatusQuery tenants) :
    IIntegrationEventHandler<StaticDataChangeSubmitted>,
    IIntegrationEventHandler<StaticDataImportSubmitted>,
    IIntegrationEventHandler<ApprovalGranted>,
    IIntegrationEventHandler<ApprovalRejected>,
    IIntegrationEventHandler<StaticDataVersionActivated>,
    IIntegrationEventHandler<StaticDataCodeDeprecated>,
    IIntegrationEventHandler<StaticDataImportCompleted>,
    IIntegrationEventHandler<StaticDataImportRejected>,
    IIntegrationEventHandler<SetupChangeSetRequested>,
    IIntegrationEventHandler<SetupChangeSetWithdrawn>,
    IIntegrationEventHandler<SetupChangeSetAbandoned>,
    IIntegrationEventHandler<StaticDataSetupBatchApplied>
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task Handle(StaticDataChangeSubmitted submitted, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var record = submitted.Record;
        var tier2MakerChecker = (await changes.Settings(cancellationToken))?.Tier2MakerChecker ?? true;
        changes.Add(StaticDataChange.SubmitChange(submitted.ChangeRequestId, record.EntityCode, StaticDataChange.KeyFor(record.Code, record.Version, record.Global),
            submitted.Action, JsonSerializer.Serialize(record, Json), submitted.MakerId, submitted.EffectiveFrom, submitted.Reason,
            MakerCheckerPolicy.RequiresChecker(submitted.Tier, tier2MakerChecker), StaticDataApprovals.ApprovalType(submitted.Tier), clock.UtcNow));
    }

    /// <summary>
    /// BR-016: an import submitted as one change. Its change request's after names the import, its line count and hash, and
    /// the table (the first line's reference), so the decision goes back to the owning module as an import decision.
    /// </summary>
    public async Task Handle(StaticDataImportSubmitted submitted, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (await changes.Get(submitted.ChangeRequestId, cancellationToken) is not null)
        {
            return;
        }

        var record = submitted.Record;
        var after = JsonSerializer.SerializeToNode(record, Json)!.AsObject();
        after["batchId"] = submitted.BatchId;
        after["lineCount"] = submitted.LineCount;
        after["hash"] = submitted.Hash;
        if (submitted.SetupSetId is { } setId)
        {
            // AIA-086: a SETUP change set's batch waits for the set's one approval. It may arrive after the set was decided: then it is applied in its turn,
            // or discarded with the set.
            after["setupSetId"] = setId;
            var batch = StaticDataChange.SubmitSetupBatch(submitted.ChangeRequestId, record.EntityCode, submitted.BatchId, submitted.Hash, after.ToJsonString(Json),
                submitted.MakerId, submitted.EffectiveFrom, submitted.Reason, setId);
            changes.Add(batch);
            if (await changes.SetupSet(setId, cancellationToken) is { } set)
            {
                if (set.Status is SetupSetStatuses.NotApproved or SetupSetStatuses.Abandoned)
                {
                    batch.RejectChange(Guid.Empty, "The setup change set was not approved, or was abandoned.", clock.UtcNow);
                }
                else
                {
                    set.BatchArrived();
                    ApproveNext(set, [batch]);
                }
            }

            return;
        }

        var tier2MakerChecker = (await changes.Settings(cancellationToken))?.Tier2MakerChecker ?? true;
        changes.Add(StaticDataChange.SubmitImport(submitted.ChangeRequestId, record.EntityCode, submitted.BatchId, submitted.Hash, after.ToJsonString(Json),
            submitted.MakerId, submitted.EffectiveFrom, submitted.Reason, MakerCheckerPolicy.RequiresChecker(submitted.Tier, tier2MakerChecker),
            StaticDataApprovals.ApprovalType(submitted.Tier), clock.UtcNow));
    }

    /// <summary>
    /// Workflow approved the change (checker other than the maker, within authority): ApproveChange, and the owning module
    /// approves the version; or the pending Tier 2 switch-off takes effect. A redelivery of a grant already applied (the
    /// change approved by this checker, or Tier 2 maker-checker already off with nothing pending) changes nothing; a change
    /// request, or a governance setting, that will not take it (it changed after it was submitted) parks the delivery
    /// (APPROVAL_NOT_APPLIED; D-5, ADR-022), and Workflow marks the approval not applied and tells the requester.
    /// </summary>
    public async Task Handle(ApprovalGranted granted, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (granted.BusinessRefType == SetupChangeSets.BusinessRefType)
        {
            await GrantSetup(granted, envelope, cancellationToken);
            return;
        }

        if (granted.BusinessRefType == StaticDataApprovals.BusinessRefType && await changes.Get(granted.BusinessRefId, cancellationToken) is { } change
            && !change.ApprovalApplied(granted.CheckerId)
            && change.ApproveChange(granted.CheckerId, requiresDifferentChecker: true, clock.UtcNow).IsFailure)
        {
            throw NotApplied(granted);
        }

        if (await DecideGovernance(granted.BusinessRefType, granted.BusinessRefId, approved: true, granted.CheckerId, cancellationToken) is { IsFailure: true })
        {
            throw NotApplied(granted);
        }
    }

    /// <summary>Workflow rejected the change: RejectChange, and the owning module returns the version to draft.</summary>
    public async Task Handle(ApprovalRejected rejected, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (rejected.BusinessRefType == SetupChangeSets.BusinessRefType)
        {
            await EndSetup(rejected, cancellationToken);
            return;
        }

        // Every ending (rejected, expired, withdrawn) rejects the change; one already rejected is a redelivery (D-5: any other refusal parks).
        if (rejected.BusinessRefType == StaticDataApprovals.BusinessRefType && await changes.Get(rejected.BusinessRefId, cancellationToken) is { } change
            && change.Status != ChangeStatus.Rejected
            && change.RejectChange(rejected.CheckerId, rejected.Reason, clock.UtcNow).IsFailure)
        {
            throw NotApplied(rejected);
        }

        if (await DecideGovernance(rejected.BusinessRefType, rejected.BusinessRefId, approved: false, rejected.CheckerId, cancellationToken) is { IsFailure: true })
        {
            throw NotApplied(rejected);
        }
    }

    /// <summary>
    /// AIA-086: a SETUP change set requested by the module that assembled it. Recorded with its batches and their hash, and put to one approval of the
    /// strictest tier, bound to that hash. A redelivery changes nothing.
    /// </summary>
    public async Task Handle(SetupChangeSetRequested requested, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (await changes.SetupSet(requested.SetId, cancellationToken) is not null)
        {
            return;
        }

        var set = SetupChangeSet.Submit(requested.SetId, requested.SourceRef, requested.MakerId,
            [.. requested.Batches.Select(b => new SetupBatchSubmitted(b.Seq, b.Table, b.EntityCode, b.BatchId, b.ChangeRequestId, b.Lines, b.Hash))],
            SetupChangeSets.ApprovalType, clock.UtcNow);
        if (set.IsFailure)
        {
            throw new PrerequisiteMissingException(PrerequisiteCodes.ApprovalNotApplied, $"SETUP:{requested.SetId}");
        }

        changes.Add(set.Value);
    }

    /// <summary>
    /// A submission withdrawn before its set was requested: every batch already submitted to it is rejected, so its module discards its drafts, and the set is
    /// recorded as not approved, so a batch that arrives later is discarded too. A set already requested isn't withdrawn here: Workflow ends its approval.
    /// </summary>
    public async Task Handle(SetupChangeSetWithdrawn withdrawn, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (await changes.SetupSet(withdrawn.SetId, cancellationToken) is { } existing)
        {
            if (existing.Status != SetupSetStatuses.NotApproved)
            {
                throw new PrerequisiteMissingException(PrerequisiteCodes.ApprovalEndingNotApplied, $"SETUP:{withdrawn.SetId}");
            }
        }
        else
        {
            changes.Add(SetupChangeSet.Withdrawn(withdrawn.SetId, withdrawn.SourceRef, withdrawn.MakerId, clock.UtcNow));
        }

        foreach (var change in (await changes.SetupBatches(withdrawn.SetId, cancellationToken)).Where(c => c.Status == ChangeStatus.PendingApproval))
        {
            change.RejectChange(Guid.Empty, "The setup's submission was withdrawn.", clock.UtcNow);
        }
    }

    /// <summary>
    /// OQ-180: a set abandoned. Each batch not in force is rejected, so its module discards its drafts: still pending, or approved but refused by its module.
    /// Batches in force stay. A redelivery changes nothing.
    /// </summary>
    public async Task Handle(SetupChangeSetAbandoned abandoned, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        foreach (var change in (await changes.SetupBatches(abandoned.SetId, cancellationToken))
                     .Where(c => c.ImportBatchId is { } batch && abandoned.Unapplied.Contains(batch) && c.Status is ChangeStatus.PendingApproval or ChangeStatus.Approved))
        {
            change.AbandonSetupBatch(abandoned.ApprovedBy, "The setup change set was abandoned before this batch was in force (OQ-180).", clock.UtcNow);
        }
    }

    /// <summary>A batch of a set is in force in its module: the set records it and approves the next batch, or completes.</summary>
    public async Task Handle(StaticDataSetupBatchApplied applied, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (await changes.SetupSet(applied.SetupSetId, cancellationToken) is not { } set)
        {
            throw new PrerequisiteMissingException(PrerequisiteCodes.ApprovalNotApplied, $"SETUP:{applied.SetupSetId}");
        }

        if (set.BatchApplied(applied.BatchId, clock.UtcNow).IsFailure)
        {
            throw new PrerequisiteMissingException(PrerequisiteCodes.ApprovalNotApplied, $"SETUP:{applied.SetupSetId}:{applied.BatchId}");
        }

        ApproveNext(set, await changes.SetupBatches(set.Id, cancellationToken));
    }

    /// <summary>
    /// The set's one approval (AIA-086): refused to the maker, and applied only to the contents it was given for (its hash, and each batch's change request
    /// with the lines' hash it was submitted with). The first batch is approved now; each later one when the one before is in force.
    /// </summary>
    private async Task GrantSetup(ApprovalGranted granted, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (await changes.SetupSet(granted.BusinessRefId, cancellationToken) is not { } set)
        {
            throw NotApplied(granted);
        }

        if (set.ApprovalApplied(granted.CheckerId))
        {
            return;
        }

        // AIA-086: only for a tenant still onboarding; a live tenant keeps per-table imports. Refused, the approval isn't applied and the set stays SUBMITTED.
        if (await tenants.UserAccess(envelope.TenantId, cancellationToken) != Tenancy.Contracts.TenantUserAccess.ConfigurationOnly)
        {
            throw NotApplied(granted);
        }

        var batches = await changes.SetupBatches(set.Id, cancellationToken);
        if (batches.Any(c => set.Batches.SingleOrDefault(b => b.ChangeRequestId == c.Id) is not { } b || b.Hash != c.ImportHash || b.BatchId != c.ImportBatchId)
            || set.Approve(granted.CheckerId, granted.SubjectHash, clock.UtcNow).IsFailure)
        {
            throw NotApplied(granted);
        }

        ApproveNext(set, batches);
    }

    /// <summary>
    /// The batch whose turn it is, when its change request has arrived and still names the lines the set was approved for: approved by the set's checker,
    /// and the owning module then puts it in force (StaticDataImportApproved with the set). A change request that names other lines is never approved.
    /// </summary>
    private void ApproveNext(SetupChangeSet set, IReadOnlyList<StaticDataChange> batches)
    {
        if (set.Next() is not { } next || batches.SingleOrDefault(c => c.Id == next.ChangeRequestId) is not { } change || change.Status != ChangeStatus.PendingApproval)
        {
            return;
        }

        if (change.ImportHash != next.Hash || change.ImportBatchId != next.BatchId
            || change.ApproveChange(set.CheckerId!.Value, requiresDifferentChecker: true, clock.UtcNow).IsFailure)
        {
            throw new PrerequisiteMissingException(PrerequisiteCodes.ApprovalNotApplied, $"SETUP:{set.Id}:{next.BatchId}");
        }
    }

    /// <summary>The set wasn't approved (rejected, expired, withdrawn): nothing of it applies, and each batch's drafts are discarded.</summary>
    private async Task EndSetup(ApprovalRejected rejected, CancellationToken cancellationToken)
    {
        if (await changes.SetupSet(rejected.BusinessRefId, cancellationToken) is not { } set
            || set.NotApproved(rejected.Ending, rejected.CheckerId, rejected.Reason, clock.UtcNow).IsFailure)
        {
            throw NotApplied(rejected);
        }

        foreach (var change in (await changes.SetupBatches(set.Id, cancellationToken)).Where(c => c.Status == ChangeStatus.PendingApproval))
        {
            change.RejectChange(rejected.CheckerId, rejected.Reason, clock.UtcNow);
        }
    }

    private static PrerequisiteMissingException NotApplied(ApprovalRejected rejected) =>
        new(PrerequisiteCodes.ApprovalEndingNotApplied, $"{rejected.ApprovalType}:{rejected.ApprovalId}");

    private static PrerequisiteMissingException NotApplied(ApprovalGranted granted) =>
        new(PrerequisiteCodes.ApprovalNotApplied, $"{granted.ApprovalType}:{granted.ApprovalId}");

    /// <summary>
    /// Workflow's decision on a pending switch-off of Tier 2 maker-checker (OQ-046). An ending other than approval with
    /// nothing pending any more is a redelivery: the setting is already as it was; so is an approval with nothing pending
    /// and Tier 2 maker-checker already off. An approval with nothing pending and it on (switched back on since) fails.
    /// </summary>
    private async Task<Result?> DecideGovernance(string businessRefType, Guid settingsId, bool approved, Guid checkerId, CancellationToken cancellationToken)
    {
        if (businessRefType != StaticDataApprovals.GovernanceBusinessRefType || await changes.Settings(cancellationToken) is not { } settings || settings.Id != settingsId)
        {
            return null;
        }

        return settings.PendingTier2MakerChecker is null && (!approved || !settings.Tier2MakerChecker) ? Result.Success() : settings.DecidePending(approved, checkerId);
    }

    public async Task Handle(StaticDataVersionActivated activated, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (activated.ChangeRequestId is { } id && await changes.Get(id, cancellationToken) is { } change)
        {
            change.ActivateVersion();
        }
    }

    public async Task Handle(StaticDataCodeDeprecated deprecated, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (deprecated.ChangeRequestId is { } id && await changes.Get(id, cancellationToken) is { } change)
        {
            change.DeprecateCode();
        }
    }

    public async Task Handle(StaticDataImportCompleted completed, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (!await changes.BatchExists(completed.BatchId, cancellationToken))
        {
            changes.Add(ImportBatch.CommitImport(completed.BatchId, completed.EntityCode, completed.FileRef, completed.RowCount, completed.SubmittedBy));
        }
    }

    public async Task Handle(StaticDataImportRejected rejected, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (!await changes.BatchExists(rejected.BatchId, cancellationToken))
        {
            changes.Add(ImportBatch.RejectImport(rejected.BatchId, rejected.EntityCode, rejected.FileRef, rejected.RowCount, rejected.SubmittedBy,
                rejected.Errors.Select(e => (e.Line, e.Field, e.Message))));
        }
    }
}
