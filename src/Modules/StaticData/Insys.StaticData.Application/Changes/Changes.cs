using System.Text.Json;
using FluentValidation;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Contracts.StaticData;
using Insys.BuildingBlocks.Domain;
using Insys.StaticData.Domain.Changes;

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

/// <summary>Command ApproveChange for the pending change of one static data version.</summary>
public sealed record ApproveChange(string EntityCode, string Code, int Version, bool Global) : ICommand<Guid>;

/// <summary>Command RejectChange: the version returns to draft for the maker.</summary>
public sealed record RejectChange(string EntityCode, string Code, int Version, bool Global, string Reason) : ICommand<Guid>;

public sealed record ListChangeRequests(string? Status, string? EntityCode, Guid? AfterId, int Limit) : IQuery<IReadOnlyList<ChangeRequestView>>;

public sealed record GetChangeRequest(Guid Id) : IQuery<ChangeRequestView>;

public sealed record ListImportBatches(Guid? AfterId, int Limit) : IQuery<IReadOnlyList<ImportBatchView>>;

public sealed class RejectChangeValidator : AbstractValidator<RejectChange>
{
    public RejectChangeValidator() => RuleFor(c => c.Reason).NotEmpty().MaximumLength(2000);
}

public static class StaticDataGovernanceErrorCodes
{
    [ErrorCode(404, "error.static_data.change_request_not_found")]
    public const string ChangeRequestNotFound = "CHANGE_REQUEST_NOT_FOUND";
}

internal static class Checker
{
    public static Result<Guid> Require(IExecutionContext execution) =>
        execution.ActorType == ActorType.User && Guid.TryParse(execution.ActorId, out var id)
            ? id
            : Error.Forbidden(ErrorCodes.Forbidden, "Changes are approved and rejected by identified users (SDM-003).");
}

internal sealed class ApproveChangeHandler(IChangeRequestRepository changes, IExecutionContext execution, IClock clock, IAuditTrail audit)
    : ICommandHandler<ApproveChange, Guid>
{
    public async Task<Result<Guid>> Handle(ApproveChange command, CancellationToken cancellationToken)
    {
        var checker = Checker.Require(execution);
        if (checker.IsFailure)
        {
            return checker.Error;
        }

        var change = await changes.FindPending(command.EntityCode, StaticDataChange.KeyFor(command.Code, command.Version, command.Global), cancellationToken);
        if (change is null)
        {
            return Error.NotFound(StaticDataGovernanceErrorCodes.ChangeRequestNotFound,
                "No change request is pending for this version. Submit it first; a just-submitted change appears within seconds.");
        }

        audit.Record(new AuditEntry("STATIC_DATA", "ApproveChange", command.EntityCode, change.Id.ToString(),
            new Dictionary<string, string?> { ["recordKey"] = change.RecordKey, ["maker"] = change.MakerId.ToString() }));
        // A change that needed no checker was already approved on submission; any pending change needs a different checker.
        var result = change.ApproveChange(checker.Value, requiresDifferentChecker: true, clock.UtcNow);
        return result.IsFailure ? result.Error : change.Id;
    }
}

internal sealed class RejectChangeHandler(IChangeRequestRepository changes, IExecutionContext execution, IClock clock, IAuditTrail audit)
    : ICommandHandler<RejectChange, Guid>
{
    public async Task<Result<Guid>> Handle(RejectChange command, CancellationToken cancellationToken)
    {
        var checker = Checker.Require(execution);
        if (checker.IsFailure)
        {
            return checker.Error;
        }

        var change = await changes.FindPending(command.EntityCode, StaticDataChange.KeyFor(command.Code, command.Version, command.Global), cancellationToken);
        if (change is null)
        {
            return Error.NotFound(StaticDataGovernanceErrorCodes.ChangeRequestNotFound, "No change request is pending for this version.");
        }

        audit.Record(new AuditEntry("STATIC_DATA", "RejectChange", command.EntityCode, change.Id.ToString(),
            new Dictionary<string, string?> { ["recordKey"] = change.RecordKey }));
        var result = change.RejectChange(checker.Value, command.Reason, clock.UtcNow);
        return result.IsFailure ? result.Error : change.Id;
    }
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
            MakerCheckerPolicy.RequiresChecker(submitted.Tier, tier2MakerChecker), clock.UtcNow));
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
