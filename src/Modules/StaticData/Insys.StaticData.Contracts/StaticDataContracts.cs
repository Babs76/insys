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
namespace Insys.StaticData.Contracts;

/// <summary>Permission codes for static data governance. The event contracts are in BuildingBlocks.Contracts.StaticData (ADR-003).</summary>
public static class StaticDataGovernancePermissions
{
    public const string ChangeRequestRead = "change_request.read";
    public const string SettingRead = "governance_setting.read";
    public const string SettingUpdate = "governance_setting.update";
}

/// <summary>Static data changes are approved in Workflow (ADR-009): one approval per change request.</summary>
/// <summary>
/// The static data approval permission, for modules that cannot see the static data endpoints' permissions
/// (BuildingBlocks.Api's StaticDataPermissions.Approve, the same value): Workflow lists it in a user's authority (BR-081).
/// </summary>
public static class StaticDataApprovalPermissions
{
    public const string Approve = "static_data.approve";
}

public static class StaticDataApprovals
{
    public const string BusinessRefType = "STATIC_DATA_CHANGE";

    /// <summary>A pending change to the tenant's governance settings (switching Tier 2 maker-checker off).</summary>
    public const string GovernanceBusinessRefType = "GOVERNANCE_SETTING";

    public const string ApprovalTypePrefix = "STATIC_DATA_T";

    public static string ApprovalType(int tier) => ApprovalTypePrefix + tier;
}

/// <summary>Public query interface: the change request pending for a static data version, read on Static Data's own connection.</summary>
public interface IStaticDataChangeLookup
{
    Task<Guid?> PendingChange(Guid tenantId, string entityCode, string code, int version, bool global, CancellationToken cancellationToken);

    /// <summary>The entity code of the table a change request (a change of one version, or an import) is for; null when there is no such change request.</summary>
    Task<string?> EntityCode(Guid tenantId, Guid changeRequestId, CancellationToken cancellationToken);
}

/// <summary>Public query interface: the public holidays of a holiday calendar (refdata.ref_public_holiday_calendars), as observed.</summary>
public interface IHolidayCalendar
{
    /// <summary>Holidays in force between the two dates, inclusive; a Sunday holiday with the rule "Next Monday if Sunday" is observed on the Monday.</summary>
    Task<IReadOnlyList<DateOnly>> Holidays(Guid tenantId, string calendarCode, DateOnly from, DateOnly to, CancellationToken cancellationToken);
}

/// <summary>Public query interface: whether a language code is in the platform's language register (refdata.ref_languages) on a day.</summary>
public interface ILanguageRegister
{
    Task<bool> Exists(string languageCode, DateOnly day, CancellationToken cancellationToken);
}

/// <summary>
/// AIA-086 (D-11): a tenant's setup applied as one SETUP change set. Its batches are imports through the existing path (BR-016), one per table; the set has one
/// approval, of the strictest tier (Tier 1), bound to a hash of every batch, and is applied batch by batch in dependency order.
/// </summary>
public static class SetupChangeSets
{
    /// <summary>The set's approval in Workflow: one for every batch. Its records are the batches' change requests, so it opens on the change request screen.</summary>
    public const string BusinessRefType = "STATIC_DATA_SETUP";

    /// <summary>Tier 1, the strictest: a set holds Tier 1 tables, and Tier 1 always needs a checker other than the maker (SDM-003).</summary>
    public static string ApprovalType => StaticDataApprovals.ApprovalType(1);

    /// <summary>A set's statuses: SUBMITTED (waiting for its approval), APPLYING (approved, its batches going in force in order), APPLIED, NOT_APPROVED.</summary>
    public const string Submitted = "SUBMITTED";
    public const string Applying = "APPLYING";
    public const string Applied = "APPLIED";
    public const string NotApproved = "NOT_APPROVED";
}

/// <summary>One batch of a set: a table's import, already submitted to the set, in the order it is applied.</summary>
public sealed record SetupChangeSetBatch(int Seq, string Table, string EntityCode, Guid BatchId, Guid ChangeRequestId, int Lines, string Hash);

/// <summary>
/// A SETUP change set to record and put to one approval (AIA-086), from the module that assembled it (Assist's wizard). Every batch has been imported and
/// submitted to the set. The maker is whoever ran the wizard, so Workflow refuses them as the checker.
/// </summary>
/// <param name="SourceRef">The setup it came from (an Assist setup run).</param>
[BuildingBlocks.Contracts.IntegrationEvent("sdm.SetupChangeSetRequested")]
public sealed record SetupChangeSetRequested(Guid SetId, Guid SourceRef, Guid MakerId, IReadOnlyList<SetupChangeSetBatch> Batches) : BuildingBlocks.Contracts.IIntegrationEvent;

/// <summary>Every batch of the set is in force.</summary>
[BuildingBlocks.Contracts.IntegrationEvent("sdm.SetupChangeSetApplied")]
public sealed record SetupChangeSetApplied(Guid SetId, Guid SourceRef, Guid CheckerId, DateTimeOffset AppliedAt) : BuildingBlocks.Contracts.IIntegrationEvent;

/// <summary>The set wasn't approved (rejected, expired or withdrawn): its drafts are discarded and nothing of it is in force.</summary>
/// <param name="Ending">REJECTED, EXPIRED or WITHDRAWN.</param>
[BuildingBlocks.Contracts.IntegrationEvent("sdm.SetupChangeSetNotApproved")]
public sealed record SetupChangeSetNotApproved(Guid SetId, Guid SourceRef, string Ending, string Reason) : BuildingBlocks.Contracts.IIntegrationEvent;

/// <param name="Open">Sets submitted or partly applied: the tenant can't go live while there is one (AIA-086).</param>
/// <param name="Applied">Sets applied in full: onboarding steps finished before one must pass their readiness again before go-live (AIA-085).</param>
public sealed record SetupChangeSetState(int Open, int Applied, DateTimeOffset? LastAppliedAt);

/// <summary>Public query interface: a tenant's SETUP change sets, read on Static Data's own connection.</summary>
public interface ISetupChangeSetState
{
    Task<SetupChangeSetState> For(Guid tenantId, CancellationToken cancellationToken);
}
