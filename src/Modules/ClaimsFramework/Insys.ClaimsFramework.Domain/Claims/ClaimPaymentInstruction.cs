/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : ClaimsFramework
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

namespace Insys.ClaimsFramework.Domain.Claims;

/// <summary>States from domain-model.json (claims › ClaimPaymentInstruction).</summary>
public enum InstructionStatus
{
    Drafted,
    PendingApproval,
    Approved,
    Rejected,
    Released,
    Paid,
    Failed,
}

public enum InstructionTrigger
{
    SubmitInstruction,
    ApprovalGranted,
    ApprovalRejected,
    ReleaseInstruction,
    PaymentSucceeded,
    PaymentFailed,
    RetriedWithNewDetails,
}

/// <summary>Asks Workflow for a second person's approval, with the amount for authority limits (SRS-002, WFL-004).</summary>
public sealed record InstructionSubmitted(Guid InstructionId, string ClaimNumber, Guid DraftedBy, decimal Amount, string Currency, string SubjectHash, DateTimeOffset OccurredAt)
    : IDomainEvent;

/// <summary>A failed payment is to be re-released to a different payee or bank details: a second person approves the new details first.</summary>
public sealed record InstructionRetryRequested(Guid InstructionId, string ClaimNumber, Guid RequestedBy, decimal Amount, string Currency, string SubjectHash,
    DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>Released: Payments adds it to a payout batch.</summary>
/// <summary>Who is paid, to which of their bank accounts (party.bank_account), and the keyed hash of its number as approved.</summary>
public sealed record PayeeAccount(Guid PartyId, Guid BankAccountId, string AccountHash);

public sealed record ClaimPaymentInstructed(Guid InstructionId, string ClaimNumber, string Domain, Guid PayeePartyId, Guid BankAccountId, decimal Amount, string Currency,
    int Attempt,
    DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record ClaimPaymentCompleted(Guid InstructionId, Guid MovementId, string ClaimNumber, string Domain, string ReserveType, decimal Amount, string Currency,
    DateOnly PaidOn, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// The ClaimPaymentInstruction aggregate (claims.claim_payment_instruction): released only after maker-checker
/// approval (SRS-002); never above the domain's approved amount; one per claim and instruction reference (PAY-001;
/// a unique index).
/// </summary>
public sealed class ClaimPaymentInstruction : AggregateRoot<Guid>
{
    public static readonly StateMachine<InstructionStatus, InstructionTrigger> Lifecycle = new StateMachine<InstructionStatus, InstructionTrigger>()
        .Allow(InstructionStatus.Drafted, InstructionTrigger.SubmitInstruction, InstructionStatus.PendingApproval)
        .Allow(InstructionStatus.PendingApproval, InstructionTrigger.ApprovalGranted, InstructionStatus.Approved)
        .Allow(InstructionStatus.PendingApproval, InstructionTrigger.ApprovalRejected, InstructionStatus.Rejected)
        .Allow(InstructionStatus.Approved, InstructionTrigger.ReleaseInstruction, InstructionStatus.Released)
        .Allow(InstructionStatus.Released, InstructionTrigger.PaymentSucceeded, InstructionStatus.Paid)
        .Allow(InstructionStatus.Released, InstructionTrigger.PaymentFailed, InstructionStatus.Failed)
        .Allow(InstructionStatus.Failed, InstructionTrigger.RetriedWithNewDetails, InstructionStatus.Released);

    private ClaimPaymentInstruction()
    {
    }

    public string ClaimNumber { get; private set; } = "";

    public string InstructionRef { get; private set; } = "";

    public Guid PayeePartyId { get; private set; }

    public decimal Amount { get; private set; }

    public decimal ApprovedAmount { get; private set; }

    public string Currency { get; private set; } = "";

    public string ReserveType { get; private set; } = "";

    public InstructionStatus Status { get; private set; }

    public Guid? ApprovalId { get; private set; }

    public Guid? PaymentId { get; private set; }

    public int Attempt { get; private set; } = 1;

    public Guid? DraftedBy { get; private set; }

    public Guid? ReleasedBy { get; private set; }

    /// <summary>The payee's bank account (party.bank_account) the approval covers.</summary>
    public Guid? BankAccountId { get; private set; }

    /// <summary>The keyed hash of that account's number, as approved; the number is held only by the Party module.</summary>
    public string? PayeeBankHash { get; private set; }

    /// <summary>A new payee or bank account waiting for approval.</summary>
    public Guid? PendingPayeePartyId { get; private set; }

    public Guid? PendingBankAccountId { get; private set; }

    public string? PendingPayeeBankHash { get; private set; }

    public Guid? RetryRequestedBy { get; private set; }

    /// <summary>What an approval of this instruction covers: the payee, their bank account and the amount.</summary>
    public string SubjectHash => Subject(PayeePartyId, BankAccountId, PayeeBankHash);

    /// <summary>What the approval of the waiting new payee or account covers.</summary>
    public string? PendingSubjectHash => PendingPayeePartyId is { } payee ? Subject(payee, PendingBankAccountId, PendingPayeeBankHash) : null;

    public PayeeAccount? Payee => BankAccountId is { } account ? new PayeeAccount(PayeePartyId, account, PayeeBankHash!) : null;

    private string Subject(Guid payee, Guid? bankAccountId, string? bankHash) =>
        ApprovalSubject.Hash("CLAIM_PAYMENT", Id, ClaimNumber, payee, bankAccountId, bankHash, Amount, Currency);

    /// <summary>Command DraftInstruction for an amount within the domain's approved amount, to the payee's bank account.</summary>
    public static Result<ClaimPaymentInstruction> DraftInstruction(Guid id, ClaimRegistration claim, string instructionRef, PayeeAccount payee, decimal amount,
        decimal approvedAmount, string currency, string reserveType, Guid draftedBy)
    {
        if (amount <= 0 || decimal.Round(amount, 2) != amount)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "A payment is a positive amount to two decimal places.");
        }

        if (amount > approvedAmount)
        {
            return Error.Validation(ClaimsErrorCodes.AboveApprovedAmount, $"{amount} {currency} is above the approved amount of {approvedAmount} {currency}.");
        }

        var instruction = new ClaimPaymentInstruction
        {
            Id = Guard.NotEmpty(id), ClaimNumber = claim.ClaimNumber, InstructionRef = Guard.MaxLength(Guard.NotNullOrWhiteSpace(instructionRef), 60),
            PayeePartyId = Guard.NotEmpty(payee.PartyId), BankAccountId = Guard.NotEmpty(payee.BankAccountId), PayeeBankHash = payee.AccountHash, Amount = amount,
            ApprovedAmount = approvedAmount, Currency = currency, ReserveType = reserveType, Status = InstructionStatus.Drafted, DraftedBy = draftedBy,
        };
        instruction.MarkChanged();
        return instruction;
    }

    /// <summary>
    /// An instruction for a settlement a domain has already had approved in Workflow by a second person, for this payee
    /// and bank account (process "General claim to settlement"): the settlement approval is the instruction's approval,
    /// so it is not asked again (ADR-009).
    /// </summary>
    public static Result<ClaimPaymentInstruction> FromApprovedSettlement(Guid id, ClaimRegistration claim, string instructionRef, PayeeAccount payee, decimal amount,
        string currency, string reserveType, Guid approvalId)
    {
        var instruction = DraftInstruction(id, claim, instructionRef, payee, amount, amount, currency, reserveType, Guid.Empty);
        if (instruction.IsFailure)
        {
            return instruction;
        }

        instruction.Value.DraftedBy = null;
        instruction.Value.Fire(InstructionTrigger.SubmitInstruction);
        instruction.Value.Decide(approved: true, approvalId, instruction.Value.SubjectHash, claim.Domain, DateTimeOffset.MinValue);
        return instruction;
    }

    /// <summary>Command SubmitInstruction: the approval is requested in Workflow, with the amount (WFL-004).</summary>
    public Result SubmitInstruction(Guid submittedBy, DateTimeOffset now)
    {
        var result = Fire(InstructionTrigger.SubmitInstruction);
        if (result.IsSuccess)
        {
            Raise(new InstructionSubmitted(Id, ClaimNumber, submittedBy, Amount, Currency, SubjectHash, now));
        }

        return result;
    }

    /// <summary>
    /// Workflow's decision. The checker was never the maker (Workflow refuses that). It applies only to the subject it
    /// was asked for: the payee, bank account and amount as they were when approval was requested.
    /// </summary>
    public Result Decide(bool approved, Guid approvalId, string? subjectHash, string domain, DateTimeOffset now)
    {
        if (PendingPayeePartyId is { } pendingPayee)
        {
            return DecidePayeeChange(approved, pendingPayee, subjectHash, domain, now);
        }

        if (subjectHash != SubjectHash)
        {
            return Error.Conflict(ClaimsErrorCodes.ApprovalSubjectChanged, "The approval was for another payee, bank account or amount.");
        }

        var result = Fire(approved ? InstructionTrigger.ApprovalGranted : InstructionTrigger.ApprovalRejected);
        if (result.IsSuccess)
        {
            ApprovalId = approvalId;
        }

        return result;
    }

    /// <summary>Command ReleaseInstruction: to Payments, within the outstanding reserve so the reserve never goes below zero.</summary>
    public Result ReleaseInstruction(Guid releasedBy, string domain, decimal outstandingReserve, DateTimeOffset now)
    {
        if (Amount > outstandingReserve)
        {
            return Error.Validation(ClaimsErrorCodes.ReserveBelowZero, $"{Amount} {Currency} exceeds the outstanding {ReserveType} reserve of {outstandingReserve}.");
        }

        var result = Fire(InstructionTrigger.ReleaseInstruction);
        if (result.IsSuccess)
        {
            ReleasedBy = releasedBy;
            Raise(new ClaimPaymentInstructed(Id, ClaimNumber, domain, PayeePartyId, BankAccountId!.Value, Amount, Currency, Attempt, now));
        }

        return result;
    }

    /// <summary>
    /// The payment for this release was created (GAP-036): its ID is known before it completes. No status change; a
    /// re-release clears it, so it is set again for the new attempt's payment.
    /// </summary>
    public void LinkPayment(Guid paymentId)
    {
        if (PaymentId is null && Status == InstructionStatus.Released)
        {
            PaymentId = paymentId;
            MarkChanged();
        }
    }

    /// <summary>Command RecordOutcome: the payment succeeded (Paid, with a payment movement) or failed.</summary>
    public Result RecordOutcome(bool succeeded, Guid paymentId, string domain, DateOnly paidOn, DateTimeOffset now)
    {
        if (Status != InstructionStatus.Released)
        {
            return Result.Success();
        }

        var result = Fire(succeeded ? InstructionTrigger.PaymentSucceeded : InstructionTrigger.PaymentFailed);
        if (result.IsSuccess)
        {
            PaymentId = paymentId;
            if (succeeded)
            {
                Raise(new ClaimPaymentCompleted(Id, Guid.CreateVersion7(), ClaimNumber, domain, ReserveType, Amount, Currency, paidOn, now));
            }
        }

        return result;
    }

    /// <summary>
    /// Transition "Retried with new details": a failed payment is released again, as a new payment. To the approved
    /// payee and bank account it is released at once; to a different payee or account it waits, Failed, for a second
    /// person to approve them in Workflow (SRS-002), and is released when they do.
    /// </summary>
    public Result Retry(PayeeAccount payee, string domain, Guid requestedBy, DateTimeOffset now)
    {
        if (Status != InstructionStatus.Failed)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Only a failed payment is retried; the instruction is {Status}.");
        }

        return payee == Payee ? Rerelease(requestedBy, domain, now) : RequestPayeeChange(payee, requestedBy, now);
    }

    /// <summary>
    /// An approved instruction not yet released, redirected to another payee or bank account (after the payee's account
    /// was superseded, say): a second person approves the new details before it can be released.
    /// </summary>
    public Result Redirect(PayeeAccount payee, Guid requestedBy, DateTimeOffset now) =>
        Status != InstructionStatus.Approved
            ? Error.Conflict(ErrorCodes.InvalidStateTransition, $"Only an approved instruction is redirected; it is {Status}. Retry a failed payment instead.")
            : payee == Payee ? Result.Success() : RequestPayeeChange(payee, requestedBy, now);

    private Result RequestPayeeChange(PayeeAccount payee, Guid requestedBy, DateTimeOffset now)
    {
        if (PendingPayeePartyId is not null)
        {
            return Error.Conflict(ClaimsErrorCodes.RetryPending, "New payee details are already waiting for approval.");
        }

        PendingPayeePartyId = Guard.NotEmpty(payee.PartyId);
        PendingBankAccountId = Guard.NotEmpty(payee.BankAccountId);
        PendingPayeeBankHash = payee.AccountHash;
        RetryRequestedBy = requestedBy;
        Raise(new InstructionRetryRequested(Id, ClaimNumber, requestedBy, Amount, Currency, PendingSubjectHash!, now));
        MarkChanged();
        return Result.Success();
    }

    private Result DecidePayeeChange(bool approved, Guid pendingPayee, string? subjectHash, string domain, DateTimeOffset now)
    {
        if (subjectHash != PendingSubjectHash)
        {
            return Error.Conflict(ClaimsErrorCodes.ApprovalSubjectChanged, "The approval was for other payee details.");
        }

        var requestedBy = RetryRequestedBy!.Value;
        var (account, hash) = (PendingBankAccountId, PendingPayeeBankHash);
        PendingPayeePartyId = null;
        PendingBankAccountId = null;
        PendingPayeeBankHash = null;
        RetryRequestedBy = null;
        MarkChanged();
        if (!approved)
        {
            return Result.Success();
        }

        PayeePartyId = pendingPayee;
        BankAccountId = account;
        PayeeBankHash = hash;
        return Status == InstructionStatus.Failed ? Rerelease(requestedBy, domain, now) : Result.Success();
    }

    private Result Rerelease(Guid releasedBy, string domain, DateTimeOffset now)
    {
        var result = Fire(InstructionTrigger.RetriedWithNewDetails);
        if (result.IsSuccess)
        {
            Attempt++;
            PaymentId = null;
            ReleasedBy = releasedBy;
            Raise(new ClaimPaymentInstructed(Id, ClaimNumber, domain, PayeePartyId, BankAccountId!.Value, Amount, Currency, Attempt, now));
        }

        return result;
    }

    private Result Fire(InstructionTrigger trigger)
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
