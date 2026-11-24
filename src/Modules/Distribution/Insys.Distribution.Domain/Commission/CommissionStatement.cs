/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Distribution
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
using System.Globalization;
using Insys.BuildingBlocks.Domain;

namespace Insys.Distribution.Domain.Commission;

/// <summary>
/// A statement's states. Approved with a positive net it is paid (Approved, then Paid); with a net of exactly zero it closes as
/// ClosedNil; with a negative net it is CarriedForward, and what it carries comes off the intermediary's next statements until it is
/// SettledByCarryForward, or is Invoiced to the intermediary (Ade, 24 November 2026, OQ-062). A payout that fails is PaymentFailed until
/// a second person grants its retry (DE-008).
/// </summary>
public enum StatementStatus
{
    Draft,
    Approved,
    Paid,
    ClosedNil,
    CarriedForward,
    SettledByCarryForward,
    Invoiced,
    PaymentFailed,
}

public enum StatementTrigger
{
    ApproveStatement,
    PayoutSettled,
    CloseNil,
    CarryForward,
    CarryForwardSettled,
    InvoiceClawback,
    PayoutFailed,
    RetryPayout,
}

/// <summary>The actions on a statement that wait for a second person in Workflow, by their approval type.</summary>
public static class StatementActions
{
    /// <summary>DE-008: paying a failed payout again (Payments.Contracts.PaymentApprovals.PayoutRetryApprovalType).</summary>
    public const string PayoutRetry = "PAYOUT_RETRY";

    /// <summary>Invoicing the intermediary for what a carried-forward statement still carries (Ade, 24 November 2026).</summary>
    public const string ClawbackInvoice = "COMMISSION_CLAWBACK_INVOICE";

    /// <summary>Ade (24 November 2026): a carried amount may be invoiced once owed this many days, unless the tenant sets its own.</summary>
    public const int DefaultInvoiceAfterDays = 90;
}

/// <summary>The intermediary's payee: its party's verified bank account (ADR-013).</summary>
public sealed record StatementPayee(Guid PartyId, Guid BankAccountId, string BankAccountHash);

/// <summary>Asks Workflow for a second person's approval of the statement, within authority (DST-004, SRS-002).</summary>
public sealed record StatementSubmitted(Guid StatementId, string IntermediaryCode, string PeriodCode, Guid RequestedBy, decimal NetAmount, string Currency,
    string SubjectHash, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record CommissionStatementApproved(Guid StatementId, string IntermediaryCode, string PeriodCode, Guid PayeePartyId, Guid BankAccountId, decimal NetAmount,
    string Currency, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>Asks Workflow for a second person's approval of an action on the statement (<see cref="StatementActions"/>).</summary>
public sealed record StatementActionRequested(Guid StatementId, string ApprovalType, Guid RequestedBy, decimal Amount, string Currency, string Description, string SubjectHash,
    DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>DE-008: the retry was granted; Payments makes a new payout for the attempt, which waits for a payout batch.</summary>
public sealed record StatementPayoutRetried(Guid StatementId, int Attempt, decimal NetAmount, string Currency, Guid PayeePartyId, Guid BankAccountId, DateTimeOffset OccurredAt)
    : IDomainEvent;

/// <summary>The clawback invoice was granted: Billing invoices the intermediary's party for what the statement still carried.</summary>
public sealed record ClawbackInvoiceRequested(Guid StatementId, string Reference, string IntermediaryCode, Guid PayerPartyId, decimal Amount, string Currency, DateOnly DueOn,
    DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// The CommissionStatement aggregate (commission.commission_statement): one per intermediary and period. Its lines are
/// the payable entitlements not yet on a statement, less the clawbacks to recover, less what earlier statements carried
/// forward (its opening line); it is approved by a second person in Workflow before payment (DST-004) and paid to the
/// intermediary's verified bank account. TODO(spec): tax on commission has no rule; it is zero (OQ-062 item 6).
/// </summary>
public sealed class CommissionStatement : AggregateRoot<Guid>
{
    public static readonly StateMachine<StatementStatus, StatementTrigger> Lifecycle = new StateMachine<StatementStatus, StatementTrigger>()
        .Allow(StatementStatus.Draft, StatementTrigger.ApproveStatement, StatementStatus.Approved)
        .Allow(StatementStatus.Draft, StatementTrigger.CloseNil, StatementStatus.ClosedNil)
        .Allow(StatementStatus.Draft, StatementTrigger.CarryForward, StatementStatus.CarriedForward)
        .Allow(StatementStatus.Approved, StatementTrigger.PayoutSettled, StatementStatus.Paid)
        .Allow(StatementStatus.Approved, StatementTrigger.PayoutFailed, StatementStatus.PaymentFailed)
        .Allow(StatementStatus.PaymentFailed, StatementTrigger.RetryPayout, StatementStatus.Approved)
        .Allow(StatementStatus.CarriedForward, StatementTrigger.CarryForwardSettled, StatementStatus.SettledByCarryForward)
        .Allow(StatementStatus.CarriedForward, StatementTrigger.InvoiceClawback, StatementStatus.Invoiced);

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

    /// <summary>The opening line: what this statement takes of the intermediary's earlier carried-forward statements.</summary>
    public decimal OpeningAmount { get; private set; }

    /// <summary>The business date the statement was carried forward; its age is measured from it.</summary>
    public DateOnly? CarriedOn { get; private set; }

    /// <summary>What the intermediary owes from this statement: its negative net, as a positive amount.</summary>
    public decimal CarriedAmount { get; private set; }

    /// <summary>What later statements, paid or closed, have absorbed of <see cref="CarriedAmount"/>.</summary>
    public decimal CarrySettledAmount { get; private set; }

    /// <summary>The later statement that takes part of the carried amount while it waits for approval and payment.</summary>
    public Guid? CarryOnStatementId { get; private set; }

    public decimal CarryOnStatementAmount { get; private set; }

    /// <summary>What a later statement may still take, or be invoiced.</summary>
    public decimal CarriedOwed => Status == StatementStatus.CarriedForward ? CarriedAmount - CarrySettledAmount - CarryOnStatementAmount : 0m;

    public DateOnly? InvoicedOn { get; private set; }

    public decimal? InvoicedAmount { get; private set; }

    /// <summary>DE-008: the reference of the payout that failed, which the person retrying types.</summary>
    public string? FailedPaymentRef { get; private set; }

    /// <summary>1 for the first payout; each granted retry is the next attempt.</summary>
    public int PayoutAttempt { get; private set; } = 1;

    /// <summary>The action waiting for Workflow's decision (<see cref="StatementActions"/>), if any.</summary>
    public string? PendingAction { get; private set; }

    public DateTimeOffset? PendingRequestedAt { get; private set; }

    public Guid? PendingRequestedBy { get; private set; }

    public string? PendingReason { get; private set; }

    /// <summary>The approval that last decided an action, so a redelivered decision changes nothing.</summary>
    public Guid? ActionApprovalId { get; private set; }

    /// <summary>A statement waiting for Workflow's decision.</summary>
    public bool ApprovalPending => Status == StatementStatus.Draft && SubmittedAt is not null;

    /// <summary>The statement's reference, typed to confirm an action on it: the intermediary and the period, e.g. BRK-1/2026-10.</summary>
    public string Reference => $"{IntermediaryCode}/{PeriodCode}";

    public string SubjectHash => ApprovalSubject.Hash("COMMISSION_STATEMENT", Id, IntermediaryCode, PeriodCode, NetAmount, Currency, PayeePartyId, BankAccountId,
        BankAccountHash);

    /// <summary>The subject of the pending action: what a second person approves, so a change since it was asked for refuses the grant.</summary>
    public string ActionSubjectHash => ApprovalSubject.Hash("COMMISSION_STATEMENT_ACTION", Id, PendingAction, ActionAmount, Currency, FailedPaymentRef, PayoutAttempt,
        PayeePartyId, BankAccountId, CarriedAmount, CarrySettledAmount);

    /// <summary>The amount the pending action moves: the net to pay again, or what is still carried to invoice.</summary>
    private decimal ActionAmount => PendingAction == StatementActions.ClawbackInvoice ? CarriedAmount - CarrySettledAmount : NetAmount;

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
    /// lines; what the intermediary's earlier statements carried forward (<paramref name="carried"/>, oldest first) is its
    /// opening line, as far as its commission covers it, then what it owes from broker changes (B-009); a draft prepared
    /// again (after a rejection) is recalculated. Then a second person's approval is requested.
    /// </summary>
    public Result PrepareStatement(IReadOnlyList<CommissionEntitlement> payable, IReadOnlyList<CommissionEntitlement> toRecover, IReadOnlyList<CommissionEntitlement> current,
        StatementPayee payee, Guid preparedBy, DateTimeOffset now, IReadOnlyList<CommissionRecovery>? owed = null, IReadOnlyList<CommissionStatement>? carried = null)
    {
        if (Status != StatementStatus.Draft || ApprovalPending)
        {
            return Error.Conflict(DistributionErrorCodes.StatementNotDraft, $"Statement {IntermediaryCode} {PeriodCode} is {Status}{(ApprovalPending ? ", awaiting approval" : "")}.");
        }

        // A draft prepared again releases its lines first; they are taken again below if still due. One clawed back
        // meanwhile simply leaves the draft: it was never paid, so there is nothing to recover.
        Release(current);

        if (payable.Any(e => e.Status != EntitlementStatus.Payable || e.StatementId is not null || e.IntermediaryCode != IntermediaryCode || e.Currency != Currency)
            || toRecover.Any(e => !e.NeedsRecovery || e.IntermediaryCode != IntermediaryCode || e.Currency != Currency)
            || (carried ?? []).Any(c => c.Id == Id || c.IntermediaryCode != IntermediaryCode || c.Currency != Currency))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "A statement takes the intermediary's own payable entitlements, clawbacks and carried statements in its currency.");
        }

        var carriedOwed = (carried ?? []).Where(c => c.CarriedOwed > 0 && c.PendingAction is null).ToList();
        if (payable.Count == 0 && toRecover.Count == 0 && carriedOwed.Count == 0)
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

        // Ade (24 November 2026): what earlier statements carried forward is the opening line, as far as this statement's commission covers it; the rest stays carried.
        OpeningAmount = 0m;
        foreach (var earlier in carriedOwed)
        {
            var taken = earlier.TakeCarriedOn(Id, Math.Max(GrossAmount, 0m));
            OpeningAmount += taken;
            GrossAmount -= taken;
        }

        // B-009: what a broker owes from a broker of record change comes off what this statement has; the rest stays owed.
        foreach (var recovery in owed ?? [])
        {
            GrossAmount -= recovery.TakeOn(Id, Math.Max(GrossAmount, 0m));
        }

        TaxAmount = 0m;
        NetAmount = GrossAmount - TaxAmount;
        PayeePartyId = payee.PartyId;
        BankAccountId = payee.BankAccountId;
        BankAccountHash = payee.BankAccountHash;
        SubmittedAt = now;
        Raise(new StatementSubmitted(Id, IntermediaryCode, PeriodCode, preparedBy, NetAmount, Currency, SubjectHash, now));
        return Result.Success();
    }

    /// <summary>True when this approval's ending (rejected, expired or withdrawn) was already applied: a redelivery changes nothing.</summary>
    public bool EndedBy(Guid approvalId) => Status == StatementStatus.Draft && !ApprovalPending && ApprovalId == approvalId;

    /// <summary>True when this approval's grant was already applied (approved, and perhaps paid, closed or carried since): a redelivered grant changes nothing.</summary>
    public bool GrantedBy(Guid approvalId) => Status != StatementStatus.Draft && ApprovalId == approvalId;

    /// <summary>
    /// Command ApproveStatement: Workflow's decision for this statement as prepared (the subject must match). Approval
    /// pays the net amount when it is positive. With a net of exactly zero the statement closes (ClosedNil); with a negative
    /// net it is carried forward from <paramref name="today"/> (Ade, 24 November 2026): either way nothing is paid, and its
    /// payable entitlements (<paramref name="onStatement"/>) are settled by it, so they are never counted again. Every other
    /// ending (rejected, expired, withdrawn) returns the statement to draft, to be prepared again, and releases its
    /// entitlements and clawback recoveries so the next prepared statement takes them; the ending approval is recorded so a
    /// redelivery changes nothing.
    /// </summary>
    public Result ApproveStatement(bool approved, Guid approvalId, Guid checkerId, string? subjectHash, DateTimeOffset now,
        IReadOnlyList<CommissionEntitlement>? onStatement = null, DateOnly? today = null)
    {
        if (!approved && EndedBy(approvalId))
        {
            return Result.Success();
        }

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
            ApprovalId = approvalId;
            Release(onStatement ?? []);
            MarkChanged();
            return Result.Success();
        }

        var result = Fire(NetAmount > 0 ? StatementTrigger.ApproveStatement : NetAmount == 0 ? StatementTrigger.CloseNil : StatementTrigger.CarryForward);
        if (result.IsFailure)
        {
            return result;
        }

        ApprovalId = approvalId;
        ApprovedBy = checkerId;
        if (NetAmount > 0)
        {
            Raise(new CommissionStatementApproved(Id, IntermediaryCode, PeriodCode, PayeePartyId!.Value, BankAccountId!.Value, NetAmount, Currency, now));
            return result;
        }

        SettleLines(onStatement ?? []);
        if (NetAmount < 0)
        {
            CarriedOn = today ?? throw new ArgumentNullException(nameof(today), "A statement is carried forward on the tenant's business date.");
            CarriedAmount = -NetAmount;
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
            SettleLines(onStatement);
        }

        return result;
    }

    /// <summary>
    /// DE-008: the payout failed. The statement is PaymentFailed with the failed payment's reference, which a retry must
    /// type; its entitlements stay on it, unpaid. A failure already recorded (the same reference) changes nothing.
    /// </summary>
    public Result PayoutFailed(string paymentRef)
    {
        if (Status == StatementStatus.PaymentFailed && FailedPaymentRef == paymentRef)
        {
            return Result.Success();
        }

        var result = Fire(StatementTrigger.PayoutFailed);
        if (result.IsSuccess)
        {
            FailedPaymentRef = Guard.MaxLength(Guard.NotNullOrWhiteSpace(paymentRef), 25);
        }

        return result;
    }

    /// <summary>
    /// Command RetryStatementPayout (DE-008): the requester types the failed payment's reference; a second person approves
    /// the retry of the net amount (PAYOUT_RETRY) in Workflow. A mismatch is refused and changes nothing.
    /// </summary>
    public Result RequestPayoutRetry(string typedPaymentRef, Guid requestedBy, DateTimeOffset now)
    {
        if (Status != StatementStatus.PaymentFailed || PendingAction is not null)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition,
                $"Statement {Reference} is {Status}{(PendingAction is null ? "" : $", with {PendingAction} awaiting approval")}: only a failed payout is paid again.");
        }

        if (!string.Equals(typedPaymentRef?.Trim(), FailedPaymentRef, StringComparison.Ordinal))
        {
            return Error.Validation(ErrorCodes.TypedReferenceMismatch, "The payment reference typed is not the reference of the statement's failed payout.");
        }

        Ask(StatementActions.PayoutRetry, requestedBy, null, now,
            $"Retry the commission payout of statement {Reference} (attempt {PayoutAttempt + 1}, failed payment {FailedPaymentRef})");
        return Result.Success();
    }

    /// <summary>
    /// Command InvoiceClawback (Ade, 24 November 2026): the requester types the statement's reference and gives a reason;
    /// a second person approves invoicing the intermediary for what the statement still carries (COMMISSION_CLAWBACK_INVOICE).
    /// Only once the intermediary is terminated, or the amount has been carried for at least <paramref name="afterDays"/>
    /// days, and not while a later statement in preparation takes part of it.
    /// </summary>
    public Result RequestClawbackInvoice(string typedReference, string reason, bool intermediaryTerminated, int afterDays, DateOnly today, Guid requestedBy,
        DateTimeOffset now)
    {
        if (Status != StatementStatus.CarriedForward || PendingAction is not null)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition,
                $"Statement {Reference} is {Status}{(PendingAction is null ? "" : $", with {PendingAction} awaiting approval")}: only a carried-forward statement is invoiced.");
        }

        if (!string.Equals(typedReference?.Trim(), Reference, StringComparison.Ordinal))
        {
            return Error.Validation(ErrorCodes.TypedReferenceMismatch, "The reference typed is not the statement's reference.");
        }

        if (CarryOnStatementAmount > 0)
        {
            return Error.Conflict(DistributionErrorCodes.CarryOnStatement,
                $"Statement {Reference}'s carried amount is partly on a later statement awaiting approval or payment; invoice it once that statement is decided.");
        }

        if (!intermediaryTerminated && today.DayNumber - CarriedOn!.Value.DayNumber < afterDays)
        {
            return Error.Validation(DistributionErrorCodes.ClawbackInvoiceNotDue,
                $"Statement {Reference} has been carried forward since {CarriedOn:yyyy-MM-dd}: it may be invoiced after {afterDays} days, or once the intermediary is terminated.");
        }

        Ask(StatementActions.ClawbackInvoice, requestedBy, Guard.MaxLength(Guard.NotNullOrWhiteSpace(reason), 500), now,
            $"Invoice intermediary {IntermediaryCode} for the commission clawback carried by statement {Reference}");
        return Result.Success();
    }

    /// <summary>True when this approval's decision of an action was already applied: a redelivery changes nothing.</summary>
    public bool ActionDecidedBy(Guid approvalId) => PendingAction is null && ActionApprovalId == approvalId;

    /// <summary>
    /// Workflow's decision of the pending action (the subject must match). A granted retry returns the statement to Approved for
    /// the next payout attempt; a granted invoice closes what it carried as Invoiced and asks Billing to invoice the intermediary.
    /// Every other ending (rejected, expired, withdrawn) clears the request and leaves the statement where it was.
    /// </summary>
    public Result DecideAction(bool approved, string approvalType, Guid approvalId, string? subjectHash, DateOnly today, DateTimeOffset now)
    {
        if (ActionDecidedBy(approvalId))
        {
            return Result.Success();
        }

        if (PendingAction != approvalType)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Statement {Reference} has no {approvalType} awaiting approval.");
        }

        if (subjectHash is not null && subjectHash != ActionSubjectHash)
        {
            return Error.Conflict(DistributionErrorCodes.StatementChanged, "The statement changed after the action's approval was requested.");
        }

        if (approved)
        {
            var amount = ActionAmount;
            var result = Fire(approvalType == StatementActions.PayoutRetry ? StatementTrigger.RetryPayout : StatementTrigger.InvoiceClawback);
            if (result.IsFailure)
            {
                return result;
            }

            if (approvalType == StatementActions.PayoutRetry)
            {
                PayoutAttempt++;
                Raise(new StatementPayoutRetried(Id, PayoutAttempt, NetAmount, Currency, PayeePartyId!.Value, BankAccountId!.Value, now));
            }
            else
            {
                InvoicedOn = today;
                InvoicedAmount = amount;
                Raise(new ClawbackInvoiceRequested(Id, Reference, IntermediaryCode, PayeePartyId!.Value, amount, Currency, today, now));
            }
        }

        (PendingAction, PendingRequestedAt, PendingRequestedBy, PendingReason) = (null, null, null, null);
        ActionApprovalId = approvalId;
        MarkChanged();
        return Result.Success();
    }

    /// <summary>A later statement takes what it can cover of what this one carries, while it waits for approval and payment.</summary>
    internal decimal TakeCarriedOn(Guid statementId, decimal available)
    {
        var taken = Math.Min(CarriedOwed, available);
        if (taken <= 0)
        {
            return 0m;
        }

        (CarryOnStatementId, CarryOnStatementAmount) = (statementId, taken);
        MarkChanged();
        return taken;
    }

    /// <summary>The later statement went back to draft: what it took is carried again.</summary>
    public void ReleaseCarry()
    {
        (CarryOnStatementId, CarryOnStatementAmount) = (null, 0m);
        MarkChanged();
    }

    /// <summary>The later statement was paid or closed: what it took is absorbed; all of it absorbed, the statement is settled by carry-forward.</summary>
    public void SettleCarry()
    {
        CarrySettledAmount += CarryOnStatementAmount;
        (CarryOnStatementId, CarryOnStatementAmount) = (null, 0m);
        MarkChanged();
        if (Status == StatementStatus.CarriedForward && CarrySettledAmount >= CarriedAmount)
        {
            Fire(StatementTrigger.CarryForwardSettled);
        }
    }

    private void Ask(string action, Guid requestedBy, string? reason, DateTimeOffset now, string description)
    {
        (PendingAction, PendingRequestedAt, PendingRequestedBy, PendingReason) = (action, now, requestedBy, reason);
        MarkChanged();
        Raise(new StatementActionRequested(Id, action, requestedBy, ActionAmount, Currency, description, ActionSubjectHash, now));
    }

    /// <summary>The statement's payable entitlements are paid: by its payout, or settled by its nil close or carry-forward.</summary>
    private void SettleLines(IReadOnlyList<CommissionEntitlement> onStatement)
    {
        foreach (var entitlement in onStatement.Where(e => e.StatementId == Id))
        {
            entitlement.Settle();
        }
    }

    /// <summary>Takes this draft's lines off it: its entitlements and the clawbacks it was to recover are free for the next statement.</summary>
    private void Release(IReadOnlyList<CommissionEntitlement> onStatement)
    {
        foreach (var entitlement in onStatement)
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
