/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Life
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
using Insys.Life.Domain.Policies;

namespace Insys.Life.Domain.Claims;

/// <summary>States from domain-model.json (life › LifeClaim).</summary>
public enum LifeClaimStatus
{
    Notified,
    Registered,
    InAssessment,
    Approved,
    Declined,
    Settled,
    Closed,
}

public enum LifeClaimTrigger
{
    RegisterClaim,
    AssessClaim,
    ApproveClaim,
    DeclineClaim,
    PaymentCompleted,
    CloseClaim,
}

/// <summary>A benefit claimed and the amount assessed for it.</summary>
public sealed record BenefitLineRequest(Guid PolicyBenefitId, decimal Amount);

/// <summary>A payee, their verified bank account (with the keyed hash of its number) and their share of the claim.</summary>
public sealed record PayeeRequest(Guid PartyId, Guid BankAccountId, string BankAccountHash, decimal SharePct);

public sealed record LifeClaimPayeeAmount(Guid PartyId, Guid BankAccountId, string BankAccountHash, decimal Amount);

public sealed record LifeClaimRegistered(Guid ClaimId, string ClaimNumber, Guid PolicyId, string ClaimType, DateOnly EventDate, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>Asks Workflow for a second person's approval of the claim within authority (SRS-002).</summary>
public sealed record LifeClaimApprovalRequested(Guid ClaimId, string ClaimNumber, Guid RequestedBy, decimal Amount, string Currency, string SubjectHash,
    DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record LifeClaimApproved(Guid ClaimId, string ClaimNumber, Guid PolicyId, decimal Amount, decimal LoanOffset, string Currency, string ReserveType,
    IReadOnlyList<LifeClaimPayeeAmount> Payees, Guid ApprovalId, Guid CheckerId, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record LifeClaimDeclined(Guid ClaimId, string ClaimNumber, string DecisionReason, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record LifeClaimSettled(Guid ClaimId, string ClaimNumber, Guid PolicyId, IReadOnlyList<Guid> PolicyBenefitIds, decimal Amount, string Currency, DateOnly PaidOn,
    DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>Entity ClaimBenefitLine (life.claim_benefit_line).</summary>
public sealed class ClaimBenefitLine : Entity<Guid>, ITenantScoped
{
    private ClaimBenefitLine()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid ClaimId { get; private set; }

    public Guid PolicyBenefitId { get; private set; }

    public decimal Amount { get; private set; }

    public long RowVersion { get; private set; }

    internal static ClaimBenefitLine Create(Guid claimId, BenefitLineRequest request) => new()
    {
        Id = Guid.CreateVersion7(), ClaimId = claimId, PolicyBenefitId = request.PolicyBenefitId, Amount = request.Amount,
    };
}

/// <summary>Entity (life.claim_payee): who is paid, into which account, and how much. Written with the approval request.</summary>
public sealed class LifeClaimPayee : Entity<Guid>, ITenantScoped
{
    private LifeClaimPayee()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid ClaimId { get; private set; }

    public Guid PartyId { get; private set; }

    public Guid BankAccountId { get; private set; }

    public string BankAccountHash { get; private set; } = "";

    public decimal SharePct { get; private set; }

    public decimal Amount { get; private set; }

    internal static LifeClaimPayee Create(Guid claimId, PayeeRequest request, decimal amount) => new()
    {
        Id = Guid.CreateVersion7(), ClaimId = claimId, PartyId = request.PartyId, BankAccountId = request.BankAccountId, BankAccountHash = request.BankAccountHash,
        SharePct = request.SharePct, Amount = amount,
    };
}

/// <summary>
/// The LifeClaim aggregate (life.claim, claim_benefit_line, claim_payee). Registration checks policy status, insured
/// life, benefit eligibility, exclusions and claimant authority (LIF-008); approval is a second person's, within
/// authority (SRS-002); the decision records its reason and rationale (CLM-003); only one claim is open per benefit and
/// event (CLM-005, checked by the application across claims).
/// </summary>
public sealed class LifeClaim : AggregateRoot<Guid>
{
    public static readonly StateMachine<LifeClaimStatus, LifeClaimTrigger> Lifecycle = new StateMachine<LifeClaimStatus, LifeClaimTrigger>()
        .Allow(LifeClaimStatus.Notified, LifeClaimTrigger.RegisterClaim, LifeClaimStatus.Registered)
        .Allow(LifeClaimStatus.Registered, LifeClaimTrigger.AssessClaim, LifeClaimStatus.InAssessment)
        .Allow(LifeClaimStatus.InAssessment, LifeClaimTrigger.ApproveClaim, LifeClaimStatus.Approved)
        .Allow(LifeClaimStatus.InAssessment, LifeClaimTrigger.DeclineClaim, LifeClaimStatus.Declined)
        .Allow(LifeClaimStatus.Approved, LifeClaimTrigger.PaymentCompleted, LifeClaimStatus.Settled)
        .Allow(LifeClaimStatus.Settled, LifeClaimTrigger.CloseClaim, LifeClaimStatus.Closed)
        .Allow(LifeClaimStatus.Declined, LifeClaimTrigger.CloseClaim, LifeClaimStatus.Closed);

    private readonly List<ClaimBenefitLine> _lines = [];
    private readonly List<LifeClaimPayee> _payees = [];

    private LifeClaim()
    {
    }

    public string? ClaimNumber { get; private set; }

    /// <summary>The policy's product and organisation node, which scope the claim (F103, addenda/access-scopes.md §1).</summary>
    public string? ProductCode { get; private set; }

    public string? OrgNodeCode { get; private set; }

    public Guid PolicyId { get; private set; }

    public string ClaimType { get; private set; } = "";

    public DateOnly EventDate { get; private set; }

    public string? CauseCode { get; private set; }

    public Guid ClaimantPartyId { get; private set; }

    public Guid InsuredLifeId { get; private set; }

    public LifeClaimStatus Status { get; private set; }

    public string Currency { get; private set; } = "";

    public DateTimeOffset NotifiedAt { get; private set; }

    public decimal? AssessedAmount { get; private set; }

    /// <summary>What is paid: the assessed amount less any policy loan it settles.</summary>
    public decimal? ApprovedAmount { get; private set; }

    public string? ReserveType { get; private set; }

    public string? DecisionReason { get; private set; }

    public string? DecisionRationale { get; private set; }

    public string? AuthorityRole { get; private set; }

    public Guid? RequestedBy { get; private set; }

    public Guid? DecidedBy { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    public Guid? ApprovalId { get; private set; }

    public decimal PaidAmount { get; private set; }

    public IReadOnlyList<ClaimBenefitLine> Lines => _lines;

    public IReadOnlyList<LifeClaimPayee> Payees => _payees;

    public bool IsOpen => Status is not (LifeClaimStatus.Declined or LifeClaimStatus.Closed);

    public bool ApprovalPending => Status == LifeClaimStatus.InAssessment && RequestedBy is not null;

    public string? SubjectHash => ApprovedAmount is null
        ? null
        : ApprovalSubject.Hash([
            "LIFE_CLAIM", Id, ClaimNumber, ApprovedAmount, Currency, ReserveType, DecisionReason,
            .. _payees.OrderBy(p => p.PartyId).SelectMany(p => new object?[] { p.PartyId, p.BankAccountId, p.BankAccountHash, p.Amount }),
        ]);

    /// <summary>Command NotifyClaim: the event, on an insured life of the policy, by a claimant. Checked in full at registration.</summary>
    public static Result<LifeClaim> NotifyClaim(Guid id, LifePolicy policy, string claimType, DateOnly eventDate, string? causeCode, Guid claimantPartyId, Guid insuredLifeId,
        DateTimeOffset now)
    {
        if (policy.Lives.All(l => l.Id != insuredLifeId))
        {
            return Error.Validation(LifeErrorCodes.NotCovered, "The insured life is not on the policy.");
        }

        if (eventDate > DateOnly.FromDateTime(now.UtcDateTime))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "The event date is not in the future.");
        }

        return new LifeClaim
        {
            Id = id, PolicyId = policy.Id, ProductCode = policy.ProductCode, OrgNodeCode = policy.OrgNodeCode, ClaimType = Guard.MaxLength(claimType, 15), EventDate = eventDate, CauseCode = causeCode, ClaimantPartyId = Guard.NotEmpty(claimantPartyId),
            InsuredLifeId = insuredLifeId, Status = LifeClaimStatus.Notified, Currency = policy.Currency, NotifiedAt = now,
        };
    }

    /// <summary>Command RegisterClaim, with the claim number the Claims Framework allocated once the checks of LIF-008 passed.</summary>
    public Result RegisterClaim(string claimNumber, DateTimeOffset now)
    {
        var result = Fire(LifeClaimTrigger.RegisterClaim);
        if (result.IsSuccess)
        {
            ClaimNumber = Guard.MaxLength(claimNumber, 25);
            Raise(new LifeClaimRegistered(Id, claimNumber, PolicyId, ClaimType, EventDate, now));
        }

        return result;
    }

    /// <summary>Command AssessClaim: the benefits claimed and the amounts, each at most the benefit's sum assured. Reassessing replaces them.</summary>
    public Result AssessClaim(IReadOnlyList<BenefitLineRequest> lines, IReadOnlyDictionary<Guid, decimal> sumsAssured)
    {
        if (ApprovalPending)
        {
            return Error.Conflict(LifeErrorCodes.DecisionPending, "The claim is waiting for approval.");
        }

        if (lines.Count == 0 || lines.Any(l => l.Amount <= 0 || !sumsAssured.TryGetValue(l.PolicyBenefitId, out var sumAssured) || l.Amount > sumAssured)
            || lines.Select(l => l.PolicyBenefitId).Distinct().Count() != lines.Count)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "Each claimed benefit is covered, once, for an amount above zero and at most its sum assured.");
        }

        if (Status == LifeClaimStatus.Registered)
        {
            var result = Fire(LifeClaimTrigger.AssessClaim);
            if (result.IsFailure)
            {
                return result;
            }
        }
        else if (Status != LifeClaimStatus.InAssessment)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"The claim is {Status}.");
        }

        _lines.Clear();
        _lines.AddRange(lines.Select(l => ClaimBenefitLine.Create(Id, l)));
        AssessedAmount = lines.Sum(l => l.Amount);
        MarkChanged();
        return Result.Success();
    }

    /// <summary>
    /// Command ApproveClaim, as the maker: the decision, its rationale, the payees and their shares (totalling 100%).
    /// A policy loan is settled from the claim first. Workflow approves within authority.
    /// </summary>
    public Result RequestApproval(IReadOnlyList<PayeeRequest> payees, decimal loanOffset, string reserveType, string decisionReason, string rationale, Guid by,
        DateTimeOffset now)
    {
        if (Status != LifeClaimStatus.InAssessment || ApprovalPending)
        {
            return Error.Conflict(ApprovalPending ? LifeErrorCodes.DecisionPending : ErrorCodes.InvalidStateTransition,
                ApprovalPending ? "The claim is already waiting for approval." : $"The claim is {Status}.");
        }

        if (string.IsNullOrWhiteSpace(rationale) || string.IsNullOrWhiteSpace(decisionReason))
        {
            return Error.Validation(LifeErrorCodes.RationaleRequired, "A decision records its reason and rationale (CLM-003).");
        }

        if (payees.Count == 0 || payees.Any(p => p.SharePct <= 0) || payees.Sum(p => p.SharePct) != 100m || payees.Select(p => p.PartyId).Distinct().Count() != payees.Count)
        {
            return Error.Validation(LifeErrorCodes.PayeeInvalid, "Each payee has one share above zero, and the shares total 100%.");
        }

        var net = AssessedAmount!.Value - loanOffset;
        if (loanOffset < 0 || net <= 0)
        {
            return Error.Validation(LifeErrorCodes.PayeeInvalid, "After the policy loan there is nothing to pay.");
        }

        _payees.Clear();
        var amounts = payees.Select(p => (Payee: p, Amount: Math.Round(net * p.SharePct / 100m, 2, MidpointRounding.AwayFromZero))).ToList();
        var largest = amounts.MaxBy(a => a.Payee.SharePct);
        _payees.AddRange(amounts.Select(a => LifeClaimPayee.Create(Id, a.Payee, a == largest ? a.Amount + net - amounts.Sum(x => x.Amount) : a.Amount)));
        ApprovedAmount = net;
        ReserveType = Guard.MaxLength(reserveType, 15);
        DecisionReason = Guard.MaxLength(decisionReason, 20);
        DecisionRationale = rationale;
        RequestedBy = by;
        Raise(new LifeClaimApprovalRequested(Id, ClaimNumber!, by, net, Currency, SubjectHash!, now));
        return Result.Success();
    }

    /// <summary>Workflow's decision: approved, the claim is Approved and paid through the Claims Framework; rejected, assessment continues.</summary>
    public Result DecideApproval(bool approved, Guid approvalId, Guid checkerId, string? subjectHash, DateTimeOffset now)
    {
        if (!ApprovalPending)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "No decision is waiting for approval.");
        }

        if (subjectHash != SubjectHash)
        {
            return Error.Conflict(LifeErrorCodes.ApprovalSubjectChanged, "The approval was for another decision.");
        }

        if (checkerId == RequestedBy)
        {
            return MakerChecker.EnsureDifferentPeople(RequestedBy.ToString()!, checkerId.ToString());
        }

        if (!approved)
        {
            ApprovedAmount = null;
            ReserveType = null;
            DecisionReason = null;
            DecisionRationale = null;
            RequestedBy = null;
            _payees.Clear();
            MarkChanged();
            return Result.Success();
        }

        var result = Fire(LifeClaimTrigger.ApproveClaim);
        if (result.IsSuccess)
        {
            ApprovalId = approvalId;
            DecidedBy = checkerId;
            DecidedAt = now;
            Raise(new LifeClaimApproved(Id, ClaimNumber!, PolicyId, ApprovedAmount!.Value, AssessedAmount!.Value - ApprovedAmount.Value, Currency, ReserveType!,
                _payees.Select(p => new LifeClaimPayeeAmount(p.PartyId, p.BankAccountId, p.BankAccountHash, p.Amount)).ToList(), approvalId, checkerId, now));
        }

        return result;
    }

    /// <summary>Command DeclineClaim, with the reason and rationale (CLM-003).</summary>
    public Result DeclineClaim(string decisionReason, string rationale, Guid by, DateTimeOffset now)
    {
        if (ApprovalPending)
        {
            return Error.Conflict(LifeErrorCodes.DecisionPending, "The claim is waiting for approval.");
        }

        if (string.IsNullOrWhiteSpace(rationale) || string.IsNullOrWhiteSpace(decisionReason))
        {
            return Error.Validation(LifeErrorCodes.RationaleRequired, "A decision records its reason and rationale (CLM-003).");
        }

        var result = Fire(LifeClaimTrigger.DeclineClaim);
        if (result.IsSuccess)
        {
            DecisionReason = Guard.MaxLength(decisionReason, 20);
            DecisionRationale = rationale;
            DecidedBy = by;
            DecidedAt = now;
            Raise(new LifeClaimDeclined(Id, ClaimNumber!, DecisionReason, now));
        }

        return result;
    }

    /// <summary>A payment completed (Claims Framework's ClaimPaymentCompleted). Once the approved amount is paid, the claim is Settled ("Payment completed").</summary>
    public Result RecordPayment(decimal amount, DateOnly paidOn, DateTimeOffset now)
    {
        if (Status != LifeClaimStatus.Approved)
        {
            return Status == LifeClaimStatus.Settled ? Result.Success() : Error.Conflict(ErrorCodes.InvalidStateTransition, $"The claim is {Status}.");
        }

        PaidAmount += amount;
        MarkChanged();
        if (PaidAmount < ApprovedAmount)
        {
            return Result.Success();
        }

        var result = Fire(LifeClaimTrigger.PaymentCompleted);
        if (result.IsSuccess)
        {
            Raise(new LifeClaimSettled(Id, ClaimNumber!, PolicyId, _lines.Select(l => l.PolicyBenefitId).ToList(), PaidAmount, Currency, paidOn, now));
        }

        return result;
    }

    public Result CloseClaim() => Fire(LifeClaimTrigger.CloseClaim);

    private Result Fire(LifeClaimTrigger trigger)
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
