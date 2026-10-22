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
public sealed record InstructionSubmitted(Guid InstructionId, string ClaimNumber, Guid DraftedBy, decimal Amount, string Currency, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>Released: Payments adds it to a payout batch.</summary>
public sealed record ClaimPaymentInstructed(Guid InstructionId, string ClaimNumber, string Domain, Guid PayeePartyId, decimal Amount, string Currency, int Attempt,
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

    /// <summary>Command DraftInstruction for an amount within the domain's approved amount.</summary>
    public static Result<ClaimPaymentInstruction> DraftInstruction(Guid id, ClaimRegistration claim, string instructionRef, Guid payee, decimal amount, decimal approvedAmount,
        string currency, string reserveType, Guid draftedBy)
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
            PayeePartyId = Guard.NotEmpty(payee), Amount = amount, ApprovedAmount = approvedAmount, Currency = currency, ReserveType = reserveType,
            Status = InstructionStatus.Drafted, DraftedBy = draftedBy,
        };
        instruction.MarkChanged();
        return instruction;
    }

    /// <summary>Command SubmitInstruction: the approval is requested in Workflow, with the amount (WFL-004).</summary>
    public Result SubmitInstruction(Guid submittedBy, DateTimeOffset now)
    {
        var result = Fire(InstructionTrigger.SubmitInstruction);
        if (result.IsSuccess)
        {
            Raise(new InstructionSubmitted(Id, ClaimNumber, submittedBy, Amount, Currency, now));
        }

        return result;
    }

    /// <summary>Workflow's decision. The checker was never the maker (Workflow refuses that).</summary>
    public Result Decide(bool approved, Guid approvalId)
    {
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
            Raise(new ClaimPaymentInstructed(Id, ClaimNumber, domain, PayeePartyId, Amount, Currency, Attempt, now));
        }

        return result;
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

    /// <summary>Transition "Retried with new details": a failed payment is released again to a corrected payee, as a new payment.</summary>
    public Result Retry(Guid payee, string domain, Guid releasedBy, DateTimeOffset now)
    {
        var result = Fire(InstructionTrigger.RetriedWithNewDetails);
        if (result.IsSuccess)
        {
            PayeePartyId = payee;
            Attempt++;
            PaymentId = null;
            ReleasedBy = releasedBy;
            Raise(new ClaimPaymentInstructed(Id, ClaimNumber, domain, payee, Amount, Currency, Attempt, now));
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
