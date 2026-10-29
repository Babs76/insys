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

    /// <summary>Granted, but its record would not take it (it changed after it was submitted): parked for someone to resolve (D-5).</summary>
    NotApplied,
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
    RecordRefused,
    RecordApplied,
}

/// <summary>
/// One row of workflow.cfg_delegation_and_approval_authority in force: a role may approve a type up to a limit, in a
/// currency (null: the tenant's base currency, OQ-112).
/// </summary>
public sealed record AuthorityGrant(string RoleCode, string ApprovalType, decimal Limit, string? Currency = null);

public sealed record ApprovalGranted(Guid ApprovalId, string BusinessRefType, Guid BusinessRefId, string ApprovalType, Guid MakerId, Guid CheckerId,
    string? SubjectHash, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// The approval ended without being granted: rejected by a checker, expired, or withdrawn (<paramref name="Ending"/>). The
/// owning module puts its record back in a defined state in each case. The checker is empty unless it was rejected.
/// </summary>
public sealed record ApprovalRejected(Guid ApprovalId, string BusinessRefType, Guid BusinessRefId, string ApprovalType, Guid MakerId, Guid CheckerId,
    string Reason, string? SubjectHash, DateTimeOffset OccurredAt, string Ending = "REJECTED") : IDomainEvent;

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
        .Allow(ApprovalStatus.Requested, ApprovalTrigger.WithdrawnByRequester, ApprovalStatus.Withdrawn)
        .Allow(ApprovalStatus.Approved, ApprovalTrigger.RecordRefused, ApprovalStatus.NotApplied)
        .Allow(ApprovalStatus.NotApplied, ApprovalTrigger.RecordApplied, ApprovalStatus.Approved);

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

    /// <summary>The consumer (module) that refused the granted approval, while it is NotApplied (D-5).</summary>
    public string? NotAppliedBy { get; private set; }

    public DateTimeOffset? NotAppliedAt { get; private set; }

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
    /// <param name="reason">Optional (BR-001): why the checker approved, kept as the decision reason like a rejection's.</param>
    public Result Approve(Guid checkerId, IReadOnlyCollection<AuthorityGrant> checkerAuthority, DateTimeOffset now, string? baseCurrency = null, string? reason = null)
    {
        var allowed = CheckChecker(checkerId, now);
        if (allowed.IsFailure)
        {
            return allowed;
        }

        var covered = CheckAuthority(checkerAuthority, baseCurrency);
        if (covered.IsFailure)
        {
            return covered;
        }

        var result = Fire(ApprovalTrigger.Approve);
        if (result.IsSuccess)
        {
            CheckerId = checkerId;
            Decision = "APPROVED";
            DecisionReason = string.IsNullOrWhiteSpace(reason) ? null : Guard.MaxLength(reason.Trim(), 2000);
            DecidedAt = now;
            Raise(new ApprovalGranted(Id, BusinessRefType, BusinessRefId, ApprovalType, MakerId, checkerId, SubjectHash, now));
        }

        return result;
    }

    /// <summary>
    /// Whether the checker could approve it now, without approving (BR-004): it is waiting, the checker is not the maker,
    /// it has not expired, and the checker's authority covers its type and amount.
    /// </summary>
    public Result CanApprove(Guid checkerId, IReadOnlyCollection<AuthorityGrant> checkerAuthority, DateTimeOffset now, string? baseCurrency = null)
    {
        if (Status != ApprovalStatus.Requested)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "The approval is not waiting for a decision.");
        }

        var allowed = CheckChecker(checkerId, now);
        return allowed.IsFailure ? allowed : CheckAuthority(checkerAuthority, baseCurrency);
    }

    private Result CheckAuthority(IReadOnlyCollection<AuthorityGrant> checkerAuthority, string? baseCurrency)
    {
        var authority = checkerAuthority.Where(a => a.ApprovalType == ApprovalType).ToList();
        if (authority.Count == 0)
        {
            return Error.Forbidden(WorkflowErrorCodes.AuthorityMissing, $"The checker has no approval authority for {ApprovalType} (WFL-004).");
        }

        // OQ-112: only limits in the approval's currency count (a limit without one is in the tenant's base currency).
        var inCurrency = Currency is null ? authority : authority.Where(a => (a.Currency ?? baseCurrency ?? Currency) == Currency).ToList();
        if (Amount is { } amount && (inCurrency.Count == 0 || inCurrency.Max(a => a.Limit) < amount))
        {
            return Error.Forbidden(WorkflowErrorCodes.AuthorityExceeded,
                $"The checker's authority for {ApprovalType} does not cover {amount} {Currency} (WFL-004).");
        }

        return Result.Success();
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

    /// <summary>
    /// Transition "Withdrawn by requester": what it was for changed (the owning module withdrew it), or the requester no
    /// longer wants it. The owning module is told, as for a rejection.
    /// </summary>
    public Result Withdraw(string reason, DateTimeOffset now)
    {
        var result = Fire(ApprovalTrigger.WithdrawnByRequester);
        if (result.IsSuccess)
        {
            Decision = "WITHDRAWN";
            DecisionReason = Guard.MaxLength(Guard.NotNullOrWhiteSpace(reason), 2000);
            DecidedAt = now;
            Raise(new ApprovalRejected(Id, BusinessRefType, BusinessRefId, ApprovalType, MakerId, Guid.Empty, DecisionReason, SubjectHash, now, "WITHDRAWN"));
        }

        return result;
    }

    /// <summary>Command WithdrawApproval by the person who asked for it, with a reason.</summary>
    public Result WithdrawByRequester(Guid by, string reason, DateTimeOffset now) =>
        by != MakerId
            ? Error.Forbidden(WorkflowErrorCodes.NotRequester, "Only the person who asked for the approval can withdraw it.")
            : Withdraw(reason, now);

    /// <summary>
    /// The granted approval's record would not take it: its delivery to the owning module parked (APPROVAL_NOT_APPLIED,
    /// D-5). The checker's decision stands; the approval waits for someone to correct the record and retry, or discard it.
    /// </summary>
    public Result MarkNotApplied(string consumer, DateTimeOffset now)
    {
        var result = Fire(ApprovalTrigger.RecordRefused);
        if (result.IsSuccess)
        {
            NotAppliedBy = Guard.MaxLength(Guard.NotNullOrWhiteSpace(consumer), 100);
            NotAppliedAt = now;
        }

        return result;
    }

    /// <summary>A retry of the parked delivery succeeded: the record took the granted approval after all.</summary>
    public Result MarkApplied()
    {
        var result = Fire(ApprovalTrigger.RecordApplied);
        if (result.IsSuccess)
        {
            NotAppliedBy = null;
            NotAppliedAt = null;
        }

        return result;
    }

    /// <summary>Command Expire: transition "Time limit reached". The owning module is told, as for a rejection.</summary>
    public Result Expire(DateTimeOffset now)
    {
        if (ExpiresAt is not { } limit || now < limit)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "The approval has not reached its time limit.");
        }

        var result = Fire(ApprovalTrigger.Expire);
        if (result.IsSuccess)
        {
            Decision = "EXPIRED";
            DecisionReason = $"No decision by {limit:yyyy-MM-dd HH:mm} UTC.";
            DecidedAt = now;
            Raise(new ApprovalRejected(Id, BusinessRefType, BusinessRefId, ApprovalType, MakerId, Guid.Empty, DecisionReason, SubjectHash, now, "EXPIRED"));
        }

        return result;
    }

    private Result CheckChecker(Guid checkerId, DateTimeOffset now)
    {
        // BR-109: the system requester is reserved; it never decides anything.
        if (checkerId == Guid.Empty || MakerChecker.IsSystem(checkerId))
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
    /// <summary>Only the person who asked for an approval can withdraw it.</summary>
    [ErrorCode(403, "error.workflow.not_requester")]
    public const string NotRequester = "APPROVAL_NOT_REQUESTER";

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

    /// <summary>OQ-138: the task closes when its record does what it asks (a due diligence review is completed through Party), not by hand.</summary>
    [ErrorCode(409, "error.workflow.task_closed_by_its_record")]
    public const string TaskClosedByItsRecord = "TASK_CLOSED_BY_ITS_RECORD";
}
