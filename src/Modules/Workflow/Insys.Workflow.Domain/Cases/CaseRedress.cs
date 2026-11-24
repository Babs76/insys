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
using System.Globalization;
using Insys.BuildingBlocks.Domain;

namespace Insys.Workflow.Domain.Cases;

/// <summary>
/// A redress's status (workflow.case_redress.status): approved by a second person, then paid through Payments (OQ-104). DE-008: PaymentFailed when its
/// payout failed, until a second person approves paying it again (back to Approved).
/// </summary>
public enum RedressStatus
{
    Requested,
    Approved,
    Rejected,
    Paid,
    PaymentFailed,
}

public enum RedressTrigger
{
    Approve,
    Reject,
    Pay,
    PaymentFail,
    RetryGranted,
}

public static class RedressTypes
{
    public const string Compensation = "COMPENSATION";
    public const string Goodwill = "GOODWILL";

    public static readonly IReadOnlyList<string> All = [Compensation, Goodwill];
}

/// <summary>The redress asks Workflow for a second person's approval (approval type CASE_REDRESS), with its amount.</summary>
public sealed record RedressApprovalRequested(Guid RedressId, Guid MakerId, decimal Amount, string Currency, string CaseNumber, string SubjectHash, DateTimeOffset OccurredAt)
    : IDomainEvent;

/// <summary>Approved: Payments pays it out (OQ-104).</summary>
public sealed record CaseRedressApproved(Guid RedressId, Guid CaseId, string CaseNumber, string RedressType, decimal Amount, string Currency, Guid PayeePartyId,
    Guid? BankAccountId, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>DE-008: paying a failed redress again asks Workflow for a second person (approval type PAYOUT_RETRY), with the redress's amount.</summary>
public sealed record RedressPayoutRetryRequested(Guid RedressId, Guid MakerId, decimal Amount, string Currency, string CaseNumber, string SubjectHash, DateTimeOffset OccurredAt)
    : IDomainEvent;

/// <summary>DE-008: the retry was granted; Payments makes a new payout for this attempt, held while the payee is not cleared (gated), as the first.</summary>
public sealed record RedressPayoutRetried(Guid RedressId, int Attempt, decimal Amount, string Currency, Guid PayeePartyId, Guid BankAccountId, DateTimeOffset OccurredAt)
    : IDomainEvent;

/// <summary>
/// Compensation or goodwill on a case (PPR 18.8.3; workflow.case_redress): money as decimal plus currency, REQUESTED →
/// APPROVED (by someone other than the requester, through a Workflow approval) or REJECTED, then PAID with the payment.
/// Its steps are written to the case's history.
/// </summary>
public sealed class CaseRedress : AggregateRoot<Guid>
{
    public static readonly StateMachine<RedressStatus, RedressTrigger> Lifecycle = new StateMachine<RedressStatus, RedressTrigger>()
        .Allow(RedressStatus.Requested, RedressTrigger.Approve, RedressStatus.Approved)
        .Allow(RedressStatus.Requested, RedressTrigger.Reject, RedressStatus.Rejected)
        .Allow(RedressStatus.Approved, RedressTrigger.Pay, RedressStatus.Paid)
        .Allow(RedressStatus.Approved, RedressTrigger.PaymentFail, RedressStatus.PaymentFailed)
        .Allow(RedressStatus.PaymentFailed, RedressTrigger.RetryGranted, RedressStatus.Approved);

    private readonly List<CaseHistoryEntry> _history = [];

    private CaseRedress()
    {
    }

    public Guid CaseId { get; private set; }

    public string RedressType { get; private set; } = "";

    public decimal Amount { get; private set; }

    public string Currency { get; private set; } = "";

    public Guid PayeePartyId { get; private set; }

    public Guid? BankAccountId { get; private set; }

    public RedressStatus Status { get; private set; }

    public Guid RequestedBy { get; private set; }

    public DateTimeOffset RequestedAt { get; private set; }

    public Guid? DecidedBy { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    public DateTimeOffset? PaidAt { get; private set; }

    public Guid? PaymentId { get; private set; }

    /// <summary>DE-008: the payout attempt: 1 for the first, 2 or more for each retry (PayoutRequested.Attempt).</summary>
    public int Attempt { get; private set; } = 1;

    /// <summary>The last failed payment: its id, its reference (what a retry asks the person to type) and why it failed.</summary>
    public Guid? FailedPaymentId { get; private set; }

    public string? FailedPaymentRef { get; private set; }

    public string? FailureReason { get; private set; }

    /// <summary>The account the latest retry pays.</summary>
    public Guid? PayoutBankAccountId { get; private set; }

    /// <summary>A retry waiting for its second person: who asked, why, and the account it would pay.</summary>
    public Guid? RetryRequestedBy { get; private set; }

    public string? RetryReason { get; private set; }

    public Guid? RetryBankAccountId { get; private set; }

    /// <summary>The approval that last decided a retry: a redelivered decision changes nothing.</summary>
    public Guid? RetryApprovalId { get; private set; }

    /// <summary>What a retry's approval is for: this redress, its next attempt, the failed payment it replaces, the amount, the payee and the account.</summary>
    public string RetrySubjectHash => ApprovalSubject.Hash("CASE_REDRESS_RETRY", Id, Attempt + 1, FailedPaymentRef, Amount, Currency, PayeePartyId, RetryBankAccountId);

    /// <summary>History rows of the case written since the redress was loaded.</summary>
    public IReadOnlyList<CaseHistoryEntry> NewHistory => _history;

    /// <summary>What the approval is for: the redress, amount, currency, payee and account. A granted approval applies only to the same.</summary>
    public string SubjectHash => ApprovalSubject.Hash(Id, RedressType, Amount, Currency, PayeePartyId, BankAccountId);

    /// <summary>Command RecordRedress: on an open case, requested for a second person's approval (OQ-104).</summary>
    public static Result<CaseRedress> Request(Guid id, ServiceCase serviceCase, string redressType, decimal amount, string currency, Guid payeePartyId, Guid? bankAccountId,
        Guid requestedBy, DateTimeOffset now)
    {
        var open = serviceCase.EnsureOpen();
        if (open.IsFailure)
        {
            return open.Error;
        }

        if (!RedressTypes.All.Contains(redressType))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, $"Redress is {string.Join(" or ", RedressTypes.All)}.");
        }

        if (amount <= 0 || decimal.Round(amount, 2) != amount)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "The amount is more than zero, to the cent.");
        }

        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "The currency is an ISO 4217 code.");
        }

        if (requestedBy == Guid.Empty)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Redress is recorded by identified users.");
        }

        var redress = new CaseRedress
        {
            Id = Guard.NotEmpty(id),
            CaseId = serviceCase.Id,
            RedressType = redressType,
            Amount = amount,
            Currency = currency.ToUpperInvariant(),
            PayeePartyId = Guard.NotEmpty(payeePartyId),
            BankAccountId = bankAccountId,
            Status = RedressStatus.Requested,
            RequestedBy = requestedBy,
            RequestedAt = now,
        };
        redress.Record(CaseEventTypes.RedressRequested, requestedBy.ToString(), now);
        redress.Raise(new RedressApprovalRequested(redress.Id, requestedBy, amount, redress.Currency, serviceCase.CaseNumber, redress.SubjectHash, now));
        return redress;
    }

    /// <summary>The approval was granted: APPROVED, and Payments is told to pay it. Refused when it is not the same subject or not waiting.</summary>
    public Result Approve(Guid checkerId, string? subjectHash, string caseNumber, DateTimeOffset now)
    {
        if (subjectHash is not null && subjectHash != SubjectHash)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "The approval was for a different redress.");
        }

        var fired = Fire(RedressTrigger.Approve);
        if (fired.IsFailure)
        {
            return fired;
        }

        DecidedBy = checkerId;
        DecidedAt = now;
        Record(CaseEventTypes.RedressApproved, checkerId.ToString(), now);
        Raise(new CaseRedressApproved(Id, CaseId, caseNumber, RedressType, Amount, Currency, PayeePartyId, BankAccountId, now));
        return Result.Success();
    }

    /// <summary>The approval ended without being granted (rejected, expired or withdrawn): REJECTED.</summary>
    public Result Reject(Guid? checkerId, string ending, DateTimeOffset now)
    {
        var fired = Fire(RedressTrigger.Reject);
        if (fired.IsFailure)
        {
            return fired;
        }

        DecidedBy = checkerId;
        DecidedAt = now;
        Record(CaseEventTypes.RedressRejected, checkerId?.ToString() ?? "system", now, ending);
        return Result.Success();
    }

    /// <summary>Payments paid it: PAID with the payment.</summary>
    public Result MarkPaid(Guid paymentId, DateTimeOffset now)
    {
        var fired = Fire(RedressTrigger.Pay);
        if (fired.IsFailure)
        {
            return fired;
        }

        PaymentId = paymentId;
        PaidAt = now;
        Record(CaseEventTypes.RedressPaid, "system", now);
        return Result.Success();
    }

    /// <summary>
    /// DE-008: its payout failed. PAYMENT_FAILED with the failed payment's reference and reason. A redelivery, or a failure when it is not waiting for a
    /// payment, changes nothing (false).
    /// </summary>
    public bool RecordPaymentFailure(Guid paymentId, string paymentRef, string reason, DateTimeOffset now)
    {
        if (Status != RedressStatus.Approved || FailedPaymentId == paymentId || Fire(RedressTrigger.PaymentFail).IsFailure)
        {
            return false;
        }

        FailedPaymentId = paymentId;
        FailedPaymentRef = Guard.MaxLength(paymentRef, 25);
        FailureReason = Guard.MaxLength(reason, 200);
        Record(CaseEventTypes.RedressPaymentFailed, "system", now, FailedPaymentRef);
        return true;
    }

    /// <summary>
    /// DE-008: asks to pay a PAYMENT_FAILED redress again. The person types the failed payment's reference (TYPED_REFERENCE_MISMATCH otherwise) and gives
    /// a reason; <paramref name="bankAccountId"/> is the payee's verified account the caller settled on. A second person approves it (PAYOUT_RETRY).
    /// </summary>
    public Result RequestPayoutRetry(string typedPaymentRef, string reason, Guid bankAccountId, Guid requestedBy, string caseNumber, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "Paying the redress again needs a reason.");
        }

        if (Status != RedressStatus.PaymentFailed)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Only a redress whose payment failed is paid again; it is {Status}.");
        }

        if (RetryRequestedBy is not null)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "Paying this redress again is already waiting for a second person.");
        }

        if (!string.Equals(typedPaymentRef?.Trim(), FailedPaymentRef, StringComparison.Ordinal))
        {
            return Error.Validation(ErrorCodes.TypedReferenceMismatch, "The reference typed is not the failed payment's reference, so nothing was done.");
        }

        RetryRequestedBy = requestedBy;
        RetryReason = Guard.MaxLength(reason, 500);
        RetryBankAccountId = Guard.NotEmpty(bankAccountId);
        Record(CaseEventTypes.RedressRetryRequested, requestedBy.ToString(), now);
        Raise(new RedressPayoutRetryRequested(Id, requestedBy, Amount, Currency, caseNumber, RetrySubjectHash, now));
        MarkChanged();
        return Result.Success();
    }

    /// <summary>True when this approval already decided a retry: a redelivery changes nothing.</summary>
    public bool RetryDecidedBy(Guid approvalId) => RetryApprovalId == approvalId;

    /// <summary>
    /// Workflow's decision on a retry. Granted: APPROVED again, and the next attempt is requested from Payments (PayoutRequested, gated as the first);
    /// rejected, expired or withdrawn: the request is cleared and it stays PAYMENT_FAILED. Refused when none waits or it was for another subject.
    /// </summary>
    public Result DecidePayoutRetry(bool approved, Guid approvalId, Guid? checkerId, string? subjectHash, string? ending, DateTimeOffset now)
    {
        if (RetryRequestedBy is null || (approved && Status != RedressStatus.PaymentFailed) || subjectHash != RetrySubjectHash)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "The approval was for another retry of the redress, or none waits.");
        }

        var account = RetryBankAccountId!.Value;
        RetryApprovalId = approvalId;
        RetryRequestedBy = null;
        RetryReason = null;
        RetryBankAccountId = null;
        if (!approved)
        {
            Record(CaseEventTypes.RedressRetryRejected, checkerId?.ToString() ?? "system", now, ending);
            MarkChanged();
            return Result.Success();
        }

        var fired = Fire(RedressTrigger.RetryGranted);
        if (fired.IsFailure)
        {
            return fired;
        }

        Attempt++;
        PayoutBankAccountId = account;
        Record(CaseEventTypes.RedressRetryApproved, checkerId?.ToString() ?? "system", now);
        Raise(new RedressPayoutRetried(Id, Attempt, Amount, Currency, PayeePartyId, account, now));
        return Result.Success();
    }

    private void Record(string eventType, string actor, DateTimeOffset now, string? detail = null) =>
        _history.Add(CaseHistoryEntry.Write(CaseId, eventType, actor, now,
            $"{RedressType} {Amount.ToString("0.00", CultureInfo.InvariantCulture)} {Currency}{(detail is null ? "" : " " + detail)}"));

    private Result Fire(RedressTrigger trigger)
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
