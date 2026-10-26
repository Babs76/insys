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
        return settings.RowVersion + 1;
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
public sealed class StaticDataGovernancePolicies(IChangeRequestRepository changes, IClock clock) :
    IIntegrationEventHandler<StaticDataChangeSubmitted>,
    IIntegrationEventHandler<ApprovalGranted>,
    IIntegrationEventHandler<ApprovalRejected>,
    IIntegrationEventHandler<StaticDataVersionActivated>,
    IIntegrationEventHandler<StaticDataCodeDeprecated>,
    IIntegrationEventHandler<StaticDataImportCompleted>,
    IIntegrationEventHandler<StaticDataImportRejected>
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
    /// Workflow approved the change (checker other than the maker, within authority): ApproveChange, and the owning module
    /// approves the version; or the pending Tier 2 switch-off takes effect. A redelivery of a grant already applied (the
    /// change approved by this checker, or Tier 2 maker-checker already off with nothing pending) changes nothing; a change
    /// request, or a governance setting, that will not take it (it changed after it was submitted) parks the delivery
    /// (APPROVAL_NOT_APPLIED; D-5, ADR-022), and Workflow marks the approval not applied and tells the requester.
    /// </summary>
    public async Task Handle(ApprovalGranted granted, EventEnvelope envelope, CancellationToken cancellationToken)
    {
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
