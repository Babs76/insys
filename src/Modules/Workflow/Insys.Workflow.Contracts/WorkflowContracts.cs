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
public sealed record ApprovalGranted(Guid ApprovalId, string BusinessRefType, Guid BusinessRefId, string ApprovalType, Guid MakerId, Guid CheckerId) : IIntegrationEvent;

[IntegrationEvent("workflow.ApprovalRejected")]
public sealed record ApprovalRejected(Guid ApprovalId, string BusinessRefType, Guid BusinessRefId, string ApprovalType, Guid MakerId, Guid CheckerId, string Reason)
    : IIntegrationEvent;

/// <summary>Consumed by Workflow itself: EscalateTask (domain-model.json).</summary>
[IntegrationEvent("workflow.SlaBreached")]
public sealed record SlaBreached(Guid ProcessInstanceId, Guid TaskId, string TaskType, string? SlaCode) : IIntegrationEvent;
