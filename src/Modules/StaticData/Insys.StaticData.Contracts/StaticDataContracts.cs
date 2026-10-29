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
