/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Workflow
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
using Insys.BuildingBlocks.Domain;

namespace Insys.Workflow.Domain.Approvals;

/// <summary>States from domain-model.json (workflow › Approval), stored upper-case in workflow.approval.status.</summary>
public enum ApprovalStatus
{
    Requested,
    Approved,
    Rejected,
    Expired,
    Withdrawn,
}

public enum ApprovalTrigger
{
    Approve,
    Reject,
    Expire,
    WithdrawnByRequester,
}

/// <summary>One row of workflow.cfg_delegation_and_approval_authority in force: a role may approve a type up to a limit.</summary>
public sealed record AuthorityGrant(string RoleCode, string ApprovalType, decimal Limit);

public sealed record ApprovalGranted(Guid ApprovalId, string BusinessRefType, Guid BusinessRefId, string ApprovalType, Guid MakerId, Guid CheckerId,
    string? SubjectHash, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record ApprovalRejected(Guid ApprovalId, string BusinessRefType, Guid BusinessRefId, string ApprovalType, Guid MakerId, Guid CheckerId,
    string Reason, string? SubjectHash, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// The Approval aggregate: maker-checker for sensitive operations (SRS-002). The checker is a different person from
/// the maker, and holds authority for the type and amount (WFL-004).
/// </summary>
public sealed class Approval : AggregateRoot<Guid>
{
    public static readonly StateMachine<ApprovalStatus, ApprovalTrigger> Lifecycle = new StateMachine<ApprovalStatus, ApprovalTrigger>()
        .Allow(ApprovalStatus.Requested, ApprovalTrigger.Approve, ApprovalStatus.Approved)
        .Allow(ApprovalStatus.Requested, ApprovalTrigger.Reject, ApprovalStatus.Rejected)
        .Allow(ApprovalStatus.Requested, ApprovalTrigger.Expire, ApprovalStatus.Expired)
        .Allow(ApprovalStatus.Requested, ApprovalTrigger.WithdrawnByRequester, ApprovalStatus.Withdrawn);

    private Approval()
    {
    }

    public string BusinessRefType { get; private set; } = "";

    public Guid BusinessRefId { get; private set; }

    public string ApprovalType { get; private set; } = "";

    public Guid MakerId { get; private set; }

    public Guid? CheckerId { get; private set; }

    public ApprovalStatus Status { get; private set; }

    /// <summary>APPROVED or REJECTED once decided.</summary>
    public string? Decision { get; private set; }

    public DateTimeOffset RequestedAt { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    public DateTimeOffset? ExpiresAt { get; private set; }

    /// <summary>The amount the checker's authority must cover, if the operation has one (stored as authority_limit).</summary>
    public decimal? Amount { get; private set; }

    public string? Currency { get; private set; }

    /// <summary>The maker's reason for the request.</summary>
    public string? Reason { get; private set; }

    public string? DecisionReason { get; private set; }

    /// <summary>
    /// A hash of exactly what is being approved (the payee and their bank details, say), set by the requester. The
    /// decision carries it back, so the requester applies it only to the same subject.
    /// </summary>
    public string? SubjectHash { get; private set; }

    /// <summary>A permission the checker must hold, besides authority for the type.</summary>
    public string? RequiredPermission { get; private set; }

    /// <summary>
    /// Whether the approval is about a scoped record (F103), and that record's product and organisation node: a checker sees
    /// and decides it only within their scope.
    /// </summary>
    public bool IsScoped { get; private set; }

    public string? ProductCode { get; private set; }

    public string? OrgNodeCode { get; private set; }

    /// <summary>Records the scope of the record the approval is about (F103), once, as it is requested.</summary>
    public void ScopeTo(string? productCode, string? orgNodeCode)
    {
        IsScoped = true;
        ProductCode = productCode is null ? null : Guard.MaxLength(productCode, 15);
        OrgNodeCode = orgNodeCode is null ? null : Guard.MaxLength(orgNodeCode, 15);
    }

    /// <summary>Command RequestApproval.</summary>
    public static Approval RequestApproval(Guid id, string businessRefType, Guid businessRefId, string approvalType, Guid makerId, decimal? amount, string? currency,
        string? reason, DateTimeOffset now, DateTimeOffset? expiresAt, string? subjectHash = null, string? requiredPermission = null)
    {
        var approval = new Approval
        {
            Id = Guard.NotEmpty(id),
            BusinessRefType = Guard.MaxLength(Guard.NotNullOrWhiteSpace(businessRefType), 30),
            BusinessRefId = Guard.NotEmpty(businessRefId),
            ApprovalType = Guard.MaxLength(Guard.NotNullOrWhiteSpace(approvalType), 30),
            MakerId = Guard.NotEmpty(makerId),
            Amount = amount,
            Currency = currency,
            Reason = reason,
            SubjectHash = subjectHash is null ? null : Guard.MaxLength(subjectHash, 64),
            RequiredPermission = requiredPermission is null ? null : Guard.MaxLength(requiredPermission, 80),
            RequestedAt = now,
            ExpiresAt = expiresAt,
            Status = ApprovalStatus.Requested,
        };
        approval.MarkChanged();
        return approval;
    }

    /// <summary>
    /// Command Approve. <paramref name="checkerAuthority"/> is what the checker's roles allow for this approval type,
    /// from the delegation and approval authority in force.
    /// </summary>
    public Result Approve(Guid checkerId, IReadOnlyCollection<AuthorityGrant> checkerAuthority, DateTimeOffset now)
    {
        var allowed = CheckChecker(checkerId, now);
        if (allowed.IsFailure)
        {
            return allowed;
        }

        var authority = checkerAuthority.Where(a => a.ApprovalType == ApprovalType).ToList();
        if (authority.Count == 0)
        {
            return Error.Forbidden(WorkflowErrorCodes.AuthorityMissing, $"The checker has no approval authority for {ApprovalType} (WFL-004).");
        }

        if (Amount is { } amount && authority.Max(a => a.Limit) < amount)
        {
            return Error.Forbidden(WorkflowErrorCodes.AuthorityExceeded,
                $"The checker's authority for {ApprovalType} does not cover {amount} {Currency} (WFL-004).");
        }

        var result = Fire(ApprovalTrigger.Approve);
        if (result.IsSuccess)
        {
            CheckerId = checkerId;
            Decision = "APPROVED";
            DecidedAt = now;
            Raise(new ApprovalGranted(Id, BusinessRefType, BusinessRefId, ApprovalType, MakerId, checkerId, SubjectHash, now));
        }

        return result;
    }

    /// <summary>Command Reject, with a reason. The checker is not the maker.</summary>
    public Result Reject(Guid checkerId, string reason, DateTimeOffset now)
    {
        var allowed = CheckChecker(checkerId, now);
        if (allowed.IsFailure)
        {
            return allowed;
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "A rejection needs a reason.");
        }

        var result = Fire(ApprovalTrigger.Reject);
        if (result.IsSuccess)
        {
            CheckerId = checkerId;
            Decision = "REJECTED";
            DecisionReason = reason;
            DecidedAt = now;
            Raise(new ApprovalRejected(Id, BusinessRefType, BusinessRefId, ApprovalType, MakerId, checkerId, reason, SubjectHash, now));
        }

        return result;
    }

    /// <summary>Transition "Withdrawn by requester": what it was for changed, so it can no longer be decided.</summary>
    public Result Withdraw() => Fire(ApprovalTrigger.WithdrawnByRequester);

    /// <summary>Command Expire: transition "Time limit reached".</summary>
    public Result Expire(DateTimeOffset now) =>
        ExpiresAt is not { } limit || now < limit
            ? Error.Conflict(ErrorCodes.InvalidStateTransition, "The approval has not reached its time limit.")
            : Fire(ApprovalTrigger.Expire);

    private Result CheckChecker(Guid checkerId, DateTimeOffset now)
    {
        if (checkerId == Guid.Empty)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Approvals are decided by an identified user.");
        }

        if (checkerId == MakerId)
        {
            return MakerChecker.EnsureDifferentPeople(MakerId.ToString(), checkerId.ToString());
        }

        return Status == ApprovalStatus.Requested && ExpiresAt is { } limit && now >= limit
            ? Error.Conflict(ErrorCodes.InvalidStateTransition, "The approval has expired.")
            : Result.Success();
    }

    private Result Fire(ApprovalTrigger trigger)
    {
        var next = Lifecycle.Fire(Status, trigger);
        if (next.IsFailure)
        {
            return next.Error;
        }

        Status = next.Value;
        MarkChanged();
        return Result.Success();
    }
}

public static class WorkflowErrorCodes
{
    [ErrorCode(403, "error.workflow.authority_missing")]
    public const string AuthorityMissing = "APPROVAL_AUTHORITY_MISSING";

    [ErrorCode(403, "error.workflow.authority_exceeded")]
    public const string AuthorityExceeded = "APPROVAL_AUTHORITY_EXCEEDED";

    [ErrorCode(422, "error.workflow.transition_not_defined")]
    public const string TransitionNotDefined = "TRANSITION_NOT_DEFINED";

    [ErrorCode(422, "error.workflow.guard_failed")]
    public const string GuardFailed = "TRANSITION_GUARD_FAILED";

    [ErrorCode(422, "error.workflow.domain_validation_failed")]
    public const string DomainValidationFailed = "DOMAIN_VALIDATION_FAILED";

    [ErrorCode(422, "error.workflow.definition_invalid")]
    public const string DefinitionInvalid = "WORKFLOW_DEFINITION_INVALID";

    [ErrorCode(409, "error.workflow.process_not_running")]
    public const string ProcessNotRunning = "PROCESS_NOT_RUNNING";
}
