using Insys.BuildingBlocks.Contracts;

namespace Insys.Compliance.Contracts;

public static class CompliancePermissions
{
    public const string ReturnRead = "regulatory_return.read";
    public const string ReturnCreate = "regulatory_return.create";
    public const string ReturnUpdate = "regulatory_return.update";
    public const string ReturnApprove = "regulatory_return.approve";
    public const string ReportRead = "report.read";
    public const string ReportCreate = "report.create";
}

/// <summary>A return approved by a compliance officer was submitted to its regulator.</summary>
[IntegrationEvent("reporting.RegulatoryReturnSubmitted")]
public sealed record RegulatoryReturnSubmitted(Guid ReturnId, string ReturnCode, string PeriodCode, string Regulator, string? RegulatorRef) : IIntegrationEvent;
