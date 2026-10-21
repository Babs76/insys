namespace Insys.StaticData.Contracts;

/// <summary>Permission codes for static data governance. The event contracts are in BuildingBlocks.Contracts.StaticData (ADR-003).</summary>
public static class StaticDataGovernancePermissions
{
    public const string ChangeRequestRead = "change_request.read";
    public const string SettingRead = "governance_setting.read";
    public const string SettingUpdate = "governance_setting.update";
}
