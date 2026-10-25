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

/// <summary>A redress's status (workflow.case_redress.status): approved by a second person, then paid through Payments (OQ-104).</summary>
public enum RedressStatus
{
    Requested,
    Approved,
    Rejected,
    Paid,
}

public enum RedressTrigger
{
    Approve,
    Reject,
    Pay,
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
        .Allow(RedressStatus.Approved, RedressTrigger.Pay, RedressStatus.Paid);

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
