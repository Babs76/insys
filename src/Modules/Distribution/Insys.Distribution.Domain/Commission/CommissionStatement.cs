using System.Globalization;
using Insys.BuildingBlocks.Domain;

namespace Insys.Distribution.Domain.Commission;

public enum StatementStatus
{
    Draft,
    Approved,
    Paid,
}

public enum StatementTrigger
{
    ApproveStatement,
    PayoutSettled,
}

/// <summary>The intermediary's payee: its party's verified bank account (ADR-013).</summary>
public sealed record StatementPayee(Guid PartyId, Guid BankAccountId, string BankAccountHash);

/// <summary>Asks Workflow for a second person's approval of the statement, within authority (DST-004, SRS-002).</summary>
public sealed record StatementSubmitted(Guid StatementId, string IntermediaryCode, string PeriodCode, Guid RequestedBy, decimal NetAmount, string Currency,
    string SubjectHash, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record CommissionStatementApproved(Guid StatementId, string IntermediaryCode, string PeriodCode, Guid PayeePartyId, Guid BankAccountId, decimal NetAmount,
    string Currency, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// The CommissionStatement aggregate (commission.commission_statement): one per intermediary and period. Its lines are
/// the payable entitlements not yet on a statement, less the clawbacks to recover; it is approved by a second person
/// in Workflow before payment (DST-004) and paid to the intermediary's verified bank account. TODO(spec): tax on
/// commission has no rule; it is zero (OQ-062 item 6).
/// </summary>
public sealed class CommissionStatement : AggregateRoot<Guid>
{
    public static readonly StateMachine<StatementStatus, StatementTrigger> Lifecycle = new StateMachine<StatementStatus, StatementTrigger>()
        .Allow(StatementStatus.Draft, StatementTrigger.ApproveStatement, StatementStatus.Approved)
        .Allow(StatementStatus.Approved, StatementTrigger.PayoutSettled, StatementStatus.Paid);

    private CommissionStatement()
    {
    }

    public string IntermediaryCode { get; private set; } = "";

    public string PeriodCode { get; private set; } = "";

    public string Currency { get; private set; } = "";

    public decimal GrossAmount { get; private set; }

    public decimal TaxAmount { get; private set; }

    public decimal NetAmount { get; private set; }

    public StatementStatus Status { get; private set; }

    public DateTimeOffset? SubmittedAt { get; private set; }

    public Guid? ApprovalId { get; private set; }

    public Guid? ApprovedBy { get; private set; }

    public Guid? PayeePartyId { get; private set; }

    public Guid? BankAccountId { get; private set; }

    public string? BankAccountHash { get; private set; }

    public Guid? DocumentId { get; private set; }

    public Guid? PaymentRef { get; private set; }

    public DateOnly? PaidOn { get; private set; }

    /// <summary>A statement waiting for Workflow's decision.</summary>
    public bool ApprovalPending => Status == StatementStatus.Draft && SubmittedAt is not null;

    public string SubjectHash => ApprovalSubject.Hash("COMMISSION_STATEMENT", Id, IntermediaryCode, PeriodCode, NetAmount, Currency, PayeePartyId, BankAccountId,
        BankAccountHash);

    /// <summary>A period is a calendar month, yyyy-MM.</summary>
    public static bool IsPeriod(string periodCode) =>
        DateOnly.TryParseExact(periodCode + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    public static CommissionStatement Open(Guid id, string intermediaryCode, string periodCode, string currency) =>
        new()
        {
            Id = Guard.NotEmpty(id), IntermediaryCode = Guard.MaxLength(intermediaryCode, 15), PeriodCode = Guard.MaxLength(periodCode, 7), Currency = currency,
            Status = StatementStatus.Draft,
        };

    /// <summary>
    /// Command PrepareStatement: the payable entitlements not yet on a statement and the clawbacks to recover become its
    /// lines; a draft prepared again (after a rejection) is recalculated. Then a second person's approval is requested.
    /// </summary>
    public Result PrepareStatement(IReadOnlyList<CommissionEntitlement> payable, IReadOnlyList<CommissionEntitlement> toRecover, IReadOnlyList<CommissionEntitlement> current,
        StatementPayee payee, Guid preparedBy, DateTimeOffset now)
    {
        if (Status != StatementStatus.Draft || ApprovalPending)
        {
            return Error.Conflict(DistributionErrorCodes.StatementNotDraft, $"Statement {IntermediaryCode} {PeriodCode} is {Status}{(ApprovalPending ? ", awaiting approval" : "")}.");
        }

        // A draft prepared again releases its lines first; they are taken again below if still due. One clawed back
        // meanwhile simply leaves the draft: it was never paid, so there is nothing to recover.
        foreach (var entitlement in current)
        {
            if (entitlement.Clawback?.StatementId == Id)
            {
                entitlement.ReleaseRecovery();
            }
            else if (entitlement.StatementId == Id)
            {
                entitlement.TakeOff();
            }
        }

        if (payable.Any(e => e.Status != EntitlementStatus.Payable || e.StatementId is not null || e.IntermediaryCode != IntermediaryCode || e.Currency != Currency)
            || toRecover.Any(e => !e.NeedsRecovery || e.IntermediaryCode != IntermediaryCode || e.Currency != Currency))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "A statement takes the intermediary's own payable entitlements and clawbacks in its currency.");
        }

        if (payable.Count == 0 && toRecover.Count == 0)
        {
            return Error.Conflict(DistributionErrorCodes.StatementEmpty, $"Intermediary {IntermediaryCode} has nothing payable or to recover in {Currency}.");
        }

        foreach (var entitlement in payable)
        {
            entitlement.PutOn(Id);
        }

        foreach (var entitlement in toRecover)
        {
            entitlement.RecoverOn(Id);
        }

        GrossAmount = payable.Sum(e => e.Amount) - toRecover.Sum(e => e.Clawback!.Amount);
        TaxAmount = 0m;
        NetAmount = GrossAmount - TaxAmount;
        PayeePartyId = payee.PartyId;
        BankAccountId = payee.BankAccountId;
        BankAccountHash = payee.BankAccountHash;
        SubmittedAt = now;
        Raise(new StatementSubmitted(Id, IntermediaryCode, PeriodCode, preparedBy, NetAmount, Currency, SubjectHash, now));
        return Result.Success();
    }

    /// <summary>
    /// Command ApproveStatement: Workflow's decision for this statement as prepared (the subject must match). Approval
    /// pays the net amount when it is positive; a rejection returns the statement to draft, to be prepared again.
    /// TODO(spec): a statement whose clawbacks exceed its commission (net not above zero) is approved without a payout;
    /// collecting the balance is not specified (OQ-062).
    /// </summary>
    public Result ApproveStatement(bool approved, Guid approvalId, Guid checkerId, string? subjectHash, DateTimeOffset now)
    {
        if (!ApprovalPending)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Statement {IntermediaryCode} {PeriodCode} is not awaiting approval.");
        }

        if (subjectHash is not null && subjectHash != SubjectHash)
        {
            return Error.Conflict(DistributionErrorCodes.StatementChanged, "The statement changed after its approval was requested.");
        }

        if (!approved)
        {
            SubmittedAt = null;
            MarkChanged();
            return Result.Success();
        }

        var result = Fire(StatementTrigger.ApproveStatement);
        if (result.IsSuccess)
        {
            ApprovalId = approvalId;
            ApprovedBy = checkerId;
            if (NetAmount > 0)
            {
                Raise(new CommissionStatementApproved(Id, IntermediaryCode, PeriodCode, PayeePartyId!.Value, BankAccountId!.Value, NetAmount, Currency, now));
            }
        }

        return result;
    }

    /// <summary>"Payout settled": the payment succeeded; the statement's payable entitlements are paid ("Statement settled").</summary>
    public Result PayoutSettled(Guid paymentId, DateOnly paidOn, IReadOnlyList<CommissionEntitlement> onStatement)
    {
        var result = Fire(StatementTrigger.PayoutSettled);
        if (result.IsSuccess)
        {
            PaymentRef = paymentId;
            PaidOn = paidOn;
            foreach (var entitlement in onStatement.Where(e => e.StatementId == Id))
            {
                entitlement.Settle();
            }
        }

        return result;
    }

    private Result Fire(StatementTrigger trigger)
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
