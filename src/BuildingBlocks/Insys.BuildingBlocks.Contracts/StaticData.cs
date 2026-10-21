namespace Insys.BuildingBlocks.Contracts.StaticData;

// The published language of static data governance (context "sdm" in domain-model.json). Owning modules raise these
// events for the static data tables in their schemas; the Static Data module keeps sdm.change_request and
// sdm.import_batch from them, and decides approvals (SDM-003). They live in the building blocks because every owning
// module raises them through the shared static data framework (ADR-003). Payloads carry references only (INT-007).

/// <summary>Where a static data record lives and how the API addresses it.</summary>
/// <param name="Global">True for a platform-global row (tenant_id IS NULL); the event's tenant is then the operator tenant.</param>
public sealed record StaticRecordRef(
    string Schema, string Table, string EntityCode, string ModuleRoute, string EntityRoute, Guid RecordId, string Code, int Version, bool Global);

[IntegrationEvent("sdm.StaticDataChangeSubmitted")]
public sealed record StaticDataChangeSubmitted(
    Guid ChangeRequestId, StaticRecordRef Record, string Action, Guid MakerId, int Tier, string Scope, DateOnly EffectiveFrom, string Reason)
    : IIntegrationEvent;

[IntegrationEvent("sdm.StaticDataChangeApproved")]
public sealed record StaticDataChangeApproved(Guid ChangeRequestId, StaticRecordRef Record, Guid CheckerId, DateTimeOffset ApprovedAt)
    : IIntegrationEvent;

/// <summary>Not in domain-model.json, which lists the RejectChange command but no event for it (OQ-028).</summary>
[IntegrationEvent("sdm.StaticDataChangeRejected")]
public sealed record StaticDataChangeRejected(Guid ChangeRequestId, StaticRecordRef Record, Guid CheckerId, string Reason) : IIntegrationEvent;

/// <summary>Published on every activation with the tenant in the envelope, so consumers can drop cached values (SDM-009).</summary>
[IntegrationEvent("sdm.StaticDataVersionActivated")]
public sealed record StaticDataVersionActivated(Guid? ChangeRequestId, StaticRecordRef Record, DateOnly EffectiveFrom, DateOnly? EffectiveTo)
    : IIntegrationEvent;

[IntegrationEvent("sdm.StaticDataCodeDeprecated")]
public sealed record StaticDataCodeDeprecated(Guid? ChangeRequestId, StaticRecordRef Record) : IIntegrationEvent;

public sealed record ImportLineError(int Line, string Field, string Message);

[IntegrationEvent("sdm.StaticDataImportCompleted")]
public sealed record StaticDataImportCompleted(Guid BatchId, string EntityCode, string FileRef, int RowCount, Guid SubmittedBy) : IIntegrationEvent;

[IntegrationEvent("sdm.StaticDataImportRejected")]
public sealed record StaticDataImportRejected(
    Guid BatchId, string EntityCode, string FileRef, int RowCount, Guid SubmittedBy, IReadOnlyList<ImportLineError> Errors) : IIntegrationEvent;
