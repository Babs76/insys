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

    public const string ApprovalTypePrefix = "STATIC_DATA_T";

    public static string ApprovalType(int tier) => ApprovalTypePrefix + tier;
}

/// <summary>Public query interface: the change request pending for a static data version, read on Static Data's own connection.</summary>
public interface IStaticDataChangeLookup
{
    Task<Guid?> PendingChange(Guid tenantId, string entityCode, string code, int version, bool global, CancellationToken cancellationToken);
}
