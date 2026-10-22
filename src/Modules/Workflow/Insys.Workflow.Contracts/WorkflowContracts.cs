using Insys.BuildingBlocks.Contracts;

namespace Insys.Workflow.Contracts;

public static class WorkflowPermissions
{
    public const string ApprovalRead = "approval.read";
    public const string ApprovalApprove = "approval.approve";
    public const string ProcessRead = "process.read";
    public const string ProcessCreate = "process.create";
    public const string ProcessUpdate = "process.update";
}

/// <summary>An approval was granted. Consumers act on their own business references only.</summary>
[IntegrationEvent("workflow.ApprovalGranted")]
public sealed record ApprovalGranted(Guid ApprovalId, string BusinessRefType, Guid BusinessRefId, string ApprovalType, Guid MakerId, Guid CheckerId,
    string? SubjectHash = null) : IIntegrationEvent;

[IntegrationEvent("workflow.ApprovalRejected")]
public sealed record ApprovalRejected(Guid ApprovalId, string BusinessRefType, Guid BusinessRefId, string ApprovalType, Guid MakerId, Guid CheckerId, string Reason, string? SubjectHash = null)
    : IIntegrationEvent;

/// <summary>Consumed by Workflow itself: EscalateTask (domain-model.json).</summary>
[IntegrationEvent("workflow.SlaBreached")]
public sealed record SlaBreached(Guid ProcessInstanceId, Guid TaskId, string TaskType, string? SlaCode) : IIntegrationEvent;

/// <summary>A role's authority for one approval: it may approve amounts up to the limit.</summary>
public sealed record ApprovalAuthority(string RoleCode, decimal Limit);

/// <summary>
/// Authority a module keeps in its own static data, for its own approval types (Life claims: life.cfg_claims_authority_limits
/// by claim type). For an approval type a source decides, Workflow uses the source's authority instead of the
/// delegation table; maker and checker still differ (SRS-002, WFL-004).
/// </summary>
public interface IApprovalAuthoritySource
{
    bool Decides(string approvalType);

    Task<IReadOnlyList<ApprovalAuthority>> For(Guid tenantId, string businessRefType, Guid businessRefId, IReadOnlyCollection<string> roleCodes, DateOnly day,
        CancellationToken cancellationToken);
}
