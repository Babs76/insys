/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Contracts
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
/// <param name="EntitlementTenant">
/// For a feature entitlement (tenant.cfg_feature_entitlements), the tenant code the row names; null for any other row. With
/// <paramref name="EntitlementFeature"/>, it lets a consumer act for that tenant and feature without reading the global row (Ade, 18 November 2026).
/// </param>
/// <param name="EntitlementFeature">For a feature entitlement, the feature the row entitles; null for any other row.</param>
public sealed record StaticDataVersionActivated(Guid? ChangeRequestId, StaticRecordRef Record, DateOnly EffectiveFrom, DateOnly? EffectiveTo,
    string? EntitlementTenant = null, string? EntitlementFeature = null) : IIntegrationEvent;

[IntegrationEvent("sdm.StaticDataCodeDeprecated")]
public sealed record StaticDataCodeDeprecated(Guid? ChangeRequestId, StaticRecordRef Record) : IIntegrationEvent;

/// <summary>
/// DE-016, OQ-204 (Ade, 25 November 2026): someone other than its maker discarded a draft left untouched for the tenant's days. Its maker is told
/// (Workflow opens an informational task assigned to them): the table, code and version, who discarded it, when, and the reason. Never raised
/// for a maker's own discard. The draft is deleted, so <see cref="StaticRecordRef.RecordId"/> names a version that no longer exists.
/// </summary>
/// <param name="TableOperation">
/// OQ-203 (Ade, 26 November 2026): the operationId of the table's page the maker's task links to, its versions not in force (List{Table}Pending, Global for a
/// platform row), at /api/v1/{moduleRoute}/static/{entityRoute}[/global]/pending; null in an event raised before it was added.
/// </param>
[IntegrationEvent("sdm.StaticDataDraftDiscarded")]
public sealed record StaticDataDraftDiscarded(StaticRecordRef Record, Guid MakerId, Guid DiscardedBy, DateTimeOffset DiscardedAt, string Reason, string? TableOperation = null)
    : IIntegrationEvent;

/// <summary>
/// OQ-203 (Ade, 26 November 2026): an import never submitted was discarded as a whole, every line deleted, by its maker or, once untouched for the tenant's
/// days, by someone else holding the table's maintain function, with a reason. Static Data marks the import DISCARDED; when someone else discarded it,
/// Workflow tells the maker (an informational task), as for a draft (OQ-204).
/// </summary>
/// <param name="Record">The table, and its first line in code order (that line no longer exists).</param>
/// <param name="Reason">Why someone else discarded it; null when its maker did.</param>
/// <param name="TableOperation">The operationId of the table's page of versions not in force (List{Table}Pending), as on <see cref="StaticDataDraftDiscarded"/>.</param>
[IntegrationEvent("sdm.StaticDataImportDiscarded")]
public sealed record StaticDataImportDiscarded(Guid BatchId, StaticRecordRef Record, int LineCount, Guid MakerId, Guid DiscardedBy, DateTimeOffset DiscardedAt,
    string? Reason, string TableOperation) : IIntegrationEvent;

public sealed record ImportLineError(int Line, string Field, string Message);

[IntegrationEvent("sdm.StaticDataImportCompleted")]
public sealed record StaticDataImportCompleted(Guid BatchId, string EntityCode, string FileRef, int RowCount, Guid SubmittedBy) : IIntegrationEvent;

[IntegrationEvent("sdm.StaticDataImportRejected")]
public sealed record StaticDataImportRejected(
    Guid BatchId, string EntityCode, string FileRef, int RowCount, Guid SubmittedBy, IReadOnlyList<ImportLineError> Errors) : IIntegrationEvent;

/// <summary>
/// BR-016: an import submitted as one change. Record is its first line, which names the table (and whether the rows are
/// global); LineCount and Hash bind the approval to the complete import.
/// </summary>
/// <param name="SetupSetId">
/// For a batch of a SETUP change set (AIA-086): the set it belongs to. Its change request takes no approval of its own: the set's one approval decides it.
/// </param>
[IntegrationEvent("sdm.StaticDataImportSubmitted")]
public sealed record StaticDataImportSubmitted(
    Guid ChangeRequestId, Guid BatchId, StaticRecordRef Record, int LineCount, string Hash, Guid MakerId, int Tier, string Scope, DateOnly EffectiveFrom, string Reason,
    Guid? SetupSetId = null)
    : IIntegrationEvent;

/// <summary>BR-016: the import's one approval; the owning module approves every line, only if they still hash the same.</summary>
/// <param name="SetupSetId">
/// For a batch of a SETUP change set (AIA-086): the owning module also puts every line in force in the same transaction, then reports
/// <see cref="StaticDataSetupBatchApplied"/>, so the set applies its next batch.
/// </param>
[IntegrationEvent("sdm.StaticDataImportApproved")]
public sealed record StaticDataImportApproved(Guid ChangeRequestId, Guid BatchId, StaticRecordRef Record, int LineCount, string Hash, Guid CheckerId, DateTimeOffset ApprovedAt,
    Guid? SetupSetId = null)
    : IIntegrationEvent;

/// <summary>BR-016: the import was not approved (rejected, expired or withdrawn); every line returns to draft.</summary>
/// <param name="SetupSetId">For a batch of a SETUP change set that wasn't approved: its drafts are discarded, so the setup can be submitted again.</param>
[IntegrationEvent("sdm.StaticDataImportNotApproved")]
public sealed record StaticDataImportNotApproved(Guid ChangeRequestId, Guid BatchId, StaticRecordRef Record, Guid CheckerId, string Reason, Guid? SetupSetId = null)
    : IIntegrationEvent;

/// <summary>AIA-086: the owning module put a SETUP change set's batch in force, every line in one transaction.</summary>
[IntegrationEvent("sdm.StaticDataSetupBatchApplied")]
public sealed record StaticDataSetupBatchApplied(Guid SetupSetId, Guid ChangeRequestId, Guid BatchId, string EntityCode, int LineCount) : IIntegrationEvent;
