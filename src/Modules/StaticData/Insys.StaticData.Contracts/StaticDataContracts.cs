namespace Insys.StaticData.Contracts;

/// <summary>Permission codes for static data governance. The event contracts are in BuildingBlocks.Contracts.StaticData (ADR-003).</summary>
public static class StaticDataGovernancePermissions
{
    public const string ChangeRequestRead = "change_request.read";
    public const string SettingRead = "governance_setting.read";
    public const string SettingUpdate = "governance_setting.update";
}

/// <summary>Static data changes are approved in Workflow (ADR-009): one approval per change request.</summary>
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
