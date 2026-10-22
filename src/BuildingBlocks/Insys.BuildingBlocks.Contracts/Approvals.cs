namespace Insys.BuildingBlocks.Contracts.Approvals;

/// <summary>
/// A request for maker-checker approval (SRS-002), published by any module through its outbox. The Workflow module
/// consumes it and creates an Approval; the decision comes back as workflow.ApprovalGranted or workflow.ApprovalRejected
/// with the same business reference. It lives in the building blocks because every module that needs approval
/// publishes it (ADR-009).
/// </summary>
/// <param name="BusinessRefType">What is being approved, for example STATIC_DATA_CHANGE or PRODUCT_VERSION (max 30).</param>
/// <param name="ApprovalType">The approval authority type, matched against workflow.cfg_delegation_and_approval_authority (max 30).</param>
/// <param name="Amount">The amount the checker's authority must cover, if any (WFL-004).</param>
/// <param name="SubjectHash">A hash of exactly what is approved (BuildingBlocks.Domain.ApprovalSubject), returned with the decision so it applies only to that.</param>
/// <param name="RequiredPermission">A permission the checker must hold besides authority for the type (bank_account.verify, for example).</param>
[IntegrationEvent("workflow.ApprovalRequested")]
public sealed record ApprovalRequested(
    string BusinessRefType, Guid BusinessRefId, string ApprovalType, Guid MakerId, decimal? Amount, string? Currency, string? Reason, string? SubjectHash = null,
    string? RequiredPermission = null)
    : IIntegrationEvent;

/// <summary>
/// The requester no longer needs a pending approval (what it was for changed): Workflow withdraws it, and it can no
/// longer be decided. A new request follows for the changed subject.
/// </summary>
[IntegrationEvent("workflow.ApprovalWithdrawn")]
public sealed record ApprovalWithdrawn(string BusinessRefType, Guid BusinessRefId, string Reason) : IIntegrationEvent;
