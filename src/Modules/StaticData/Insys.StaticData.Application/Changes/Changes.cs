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

public sealed record GovernanceSettingsView(bool Tier2MakerChecker, bool Tier1MakerChecker, long RowVersion);

/// <summary>Reads the tenant's static data governance settings (defaults when never changed).</summary>
public sealed record GetGovernanceSettings : IQuery<GovernanceSettingsView>;

/// <summary>Switches Tier 2 maker-checker on or off for the tenant (SDM-003). Every change is audited.</summary>
public sealed record ChangeTier2MakerChecker(bool Enabled, long ExpectedVersion) : ICommand<long>, IVersionedCommand;

internal sealed class GetGovernanceSettingsHandler(IChangeRequestRepository repository) : IQueryHandler<GetGovernanceSettings, GovernanceSettingsView>
{
    public async Task<Result<GovernanceSettingsView>> Handle(GetGovernanceSettings query, CancellationToken cancellationToken) =>
        await repository.Settings(cancellationToken) is { } settings
            ? new GovernanceSettingsView(settings.Tier2MakerChecker, true, settings.RowVersion)
            : new GovernanceSettingsView(true, true, 0);
}

internal sealed class ChangeTier2MakerCheckerHandler(IChangeRequestRepository repository, IExecutionContext execution, IAuditTrail audit)
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
        if (!settings.ChangeTier2MakerChecker(command.Enabled) && settings.RowVersion > 0)
        {
            return settings.RowVersion;
        }

        audit.Record(new AuditEntry("STATIC_DATA_GOVERNANCE", "ChangeTier2MakerChecker", nameof(TenantGovernanceSettings), settings.Id.ToString(),
            new Dictionary<string, string?> { ["before"] = before.ToString(), ["after"] = command.Enabled.ToString() }));
        return settings.RowVersion + 1;
    }
}

public sealed record ChangeRequestView(
    Guid Id, string RequestNumber, string EntityCode, string RecordKey, string Action, string Status, Guid MakerId, Guid? CheckerId, DateOnly EffectiveFrom,
    string Reason, JsonElement Record, long RowVersion);

public sealed record ImportBatchView(Guid Id, string BatchNumber, string EntityCode, string FileRef, int RowCount, int RejectedCount, string Status,
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

internal sealed class GetChangeRequestHandler(IChangeRequestQueries queries) : IQueryHandler<GetChangeRequest, ChangeRequestView>
{
    public async Task<Result<ChangeRequestView>> Handle(GetChangeRequest query, CancellationToken cancellationToken) =>
        await queries.Get(query.Id, cancellationToken) is { } view ? view : Error.NotFound(ErrorCodes.NotFound, "The change request was not found.");
}

internal sealed class ListImportBatchesHandler(IChangeRequestQueries queries) : IQueryHandler<ListImportBatches, IReadOnlyList<ImportBatchView>>
{
    public async Task<Result<IReadOnlyList<ImportBatchView>>> Handle(ListImportBatches query, CancellationToken cancellationToken) =>
        Result.Success(await queries.Imports(query.AfterId, query.Limit, cancellationToken));
}

/// <summary>
/// The Static Data module's policies for the owning modules' events. Each runs behind the inbox, so a duplicate
/// delivery changes nothing (INT-005); a change request in an unexpected state is left as it is.
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

    /// <summary>Workflow approved the change (checker other than the maker, within authority): ApproveChange.</summary>
    public async Task Handle(ApprovalGranted granted, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (granted.BusinessRefType == StaticDataApprovals.BusinessRefType && await changes.Get(granted.BusinessRefId, cancellationToken) is { } change)
        {
            change.ApproveChange(granted.CheckerId, requiresDifferentChecker: true, clock.UtcNow);
        }
    }

    /// <summary>Workflow rejected the change: RejectChange, and the owning module returns the version to draft.</summary>
    public async Task Handle(ApprovalRejected rejected, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (rejected.BusinessRefType == StaticDataApprovals.BusinessRefType && await changes.Get(rejected.BusinessRefId, cancellationToken) is { } change)
        {
            change.RejectChange(rejected.CheckerId, rejected.Reason, clock.UtcNow);
        }
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
