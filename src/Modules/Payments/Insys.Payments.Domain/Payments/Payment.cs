/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Payments
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

namespace Insys.Payments.Domain.Payments;

/// <summary>States from domain-model.json (payments › Payment).</summary>
public enum PaymentStatus
{
    Initiated,
    Pending,
    Succeeded,
    Failed,
    Unknown,
    Reversed,
}

public enum PaymentTrigger
{
    SentToProvider,
    ProviderConfirms,
    ProviderRejects,
    NoOrAmbiguousResponse,
    ResolveUnknownSucceeded,
    ResolveUnknownFailed,
    ReversePayment,
}

public enum Direction
{
    In,
    Out,
}

/// <summary>What a provider said when a payment was sent, or in a callback, mapped to the platform's terms.</summary>
public enum ProviderOutcome
{
    /// <summary>Accepted and still in progress.</summary>
    Pending,
    Succeeded,
    Failed,

    /// <summary>No answer, an error, or a status with no mapping: never treated as succeeded (PAY-005).</summary>
    Unknown,
}

public sealed record PaymentConfirmed(Guid PaymentId, string PaymentRef, Direction Direction, string Purpose, decimal Amount, string Currency, DateOnly ValueDate,
    Guid? CounterpartyPartyId, string? BusinessRefType, Guid? BusinessRefId, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record PaymentFailed(Guid PaymentId, string PaymentRef, Direction Direction, string Purpose, decimal Amount, string Currency, string? BusinessRefType,
    Guid? BusinessRefId, string Reason, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record PaymentOutcomeUnknown(Guid PaymentId, string PaymentRef, Direction Direction, string Purpose, decimal Amount, string Currency, DateTimeOffset OccurredAt)
    : IDomainEvent;

public sealed record PaymentAllocated(Guid PaymentId, Guid AllocationId, Guid InvoiceId, decimal Amount, string Currency, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record PaymentReversed(Guid PaymentId, string PaymentRef, Direction Direction, string Purpose, decimal Amount, string Currency, DateOnly ReversedOn,
    IReadOnlyList<(Guid AllocationId, Guid InvoiceId, decimal Amount)> Allocations, string Reason, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>Entity StatusChange (payment.payment_status_history): every status change, never changed itself (PAY-002, V0014).</summary>
public sealed class StatusChange : Entity<Guid>, ITenantScoped
{
    private StatusChange()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid PaymentId { get; private set; }

    public PaymentStatus FromStatus { get; private set; }

    public PaymentStatus ToStatus { get; private set; }

    /// <summary>What caused it: PROVIDER, CALLBACK, OPERATOR, TIMEOUT, SYSTEM.</summary>
    public string Source { get; private set; } = "";

    public DateTimeOffset ChangedAt { get; private set; }

    internal static StatusChange Create(Guid paymentId, PaymentStatus from, PaymentStatus to, string source, DateTimeOffset now) =>
        new() { Id = Guid.CreateVersion7(), PaymentId = paymentId, FromStatus = from, ToStatus = to, Source = source, ChangedAt = now };
}

/// <summary>Entity PaymentAllocation (payment.payment_allocation): part of a received amount applied to an invoice (BIL-003).</summary>
public sealed class PaymentAllocation : Entity<Guid>, ITenantScoped
{
    private PaymentAllocation()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid PaymentId { get; private set; }

    public Guid InvoiceId { get; private set; }

    public decimal Amount { get; private set; }

    public string Currency { get; private set; } = "";

    public string AllocationRule { get; private set; } = "";

    public DateTimeOffset AllocatedAt { get; private set; }

    /// <summary>Set when the payment is reversed: the allocation no longer applies.</summary>
    public Guid? ReversedById { get; private set; }

    public long RowVersion { get; private set; }

    internal static PaymentAllocation Create(Guid paymentId, Guid invoiceId, decimal amount, string currency, string rule, DateTimeOffset now) =>
        new() { Id = Guid.CreateVersion7(), PaymentId = paymentId, InvoiceId = invoiceId, Amount = amount, Currency = currency, AllocationRule = rule, AllocatedAt = now };

    internal void Reverse(Guid reversalId) => ReversedById = reversalId;
}

/// <summary>
/// The Payment aggregate (payment.payment). Idempotent: the same key never moves money twice (PAY-001, a unique
/// index); every status change is recorded (PAY-002); an ambiguous outcome is Unknown and goes to reconciliation,
/// never Succeeded (PAY-005); final statuses change only by an explicit reversal; allocations never exceed the amount
/// received (BIL-003).
/// </summary>
public sealed class Payment : AggregateRoot<Guid>
{
    public static readonly StateMachine<PaymentStatus, PaymentTrigger> Lifecycle = new StateMachine<PaymentStatus, PaymentTrigger>()
        .Allow(PaymentStatus.Initiated, PaymentTrigger.SentToProvider, PaymentStatus.Pending)
        .Allow(PaymentStatus.Pending, PaymentTrigger.ProviderConfirms, PaymentStatus.Succeeded)
        .Allow(PaymentStatus.Pending, PaymentTrigger.ProviderRejects, PaymentStatus.Failed)
        .Allow(PaymentStatus.Pending, PaymentTrigger.NoOrAmbiguousResponse, PaymentStatus.Unknown)
        .Allow(PaymentStatus.Unknown, PaymentTrigger.ResolveUnknownSucceeded, PaymentStatus.Succeeded)
        .Allow(PaymentStatus.Unknown, PaymentTrigger.ResolveUnknownFailed, PaymentStatus.Failed)
        .Allow(PaymentStatus.Succeeded, PaymentTrigger.ReversePayment, PaymentStatus.Reversed);

    private readonly List<StatusChange> _history = [];
    private readonly List<PaymentAllocation> _allocations = [];

    private Payment()
    {
    }

    public string PaymentRef { get; private set; } = "";

    public Direction Direction { get; private set; }

    public string Purpose { get; private set; } = "";

    public string MethodCode { get; private set; } = "";

    public string ProviderCode { get; private set; } = "";

    public decimal Amount { get; private set; }

    public string Currency { get; private set; } = "";

    public PaymentStatus Status { get; private set; }

    public string? ExternalRef { get; private set; }

    public string IdempotencyKey { get; private set; } = "";

    public Guid? CounterpartyPartyId { get; private set; }

    public DateTimeOffset InitiatedAt { get; private set; }

    public DateOnly? ValueDate { get; private set; }

    public string? BusinessRefType { get; private set; }

    public Guid? BusinessRefId { get; private set; }

    public Guid? PayoutBatchId { get; private set; }

    /// <summary>For a payout: the payee's bank account (party.bank_account), by id. Its details are read from the Party module only to send it.</summary>
    public Guid? BankAccountId { get; private set; }

    public IReadOnlyList<StatusChange> History => _history;

    public IReadOnlyList<PaymentAllocation> Allocations => _allocations;

    /// <summary>The allocation rule of a reversing allocation row (payment_allocation.reversed_by_id points to it).</summary>
    public const string ReversalRule = "REVERSAL";

    public decimal Allocated => _allocations.Where(a => a.ReversedById is null && a.AllocationRule != ReversalRule).Sum(a => a.Amount);

    public bool IsFinal => Status is PaymentStatus.Succeeded or PaymentStatus.Failed or PaymentStatus.Reversed;

    /// <summary>Command InitiatePayment. The caller checks the idempotency key first; the database refuses a second payment with it.</summary>
    public static Result<Payment> InitiatePayment(Guid id, string idempotencyKey, Direction direction, string purpose, string methodCode, string providerCode,
        decimal amount, string currency, Guid? counterparty, string? businessRefType, Guid? businessRefId, DateTimeOffset now, Guid? bankAccountId = null)
    {
        if (amount <= 0 || decimal.Round(amount, 2) != amount)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "A payment is a positive amount to two decimal places.");
        }

        var payment = new Payment
        {
            Id = Guard.NotEmpty(id),
            PaymentRef = "PAY-" + id.ToString("N")[..16].ToUpperInvariant(),
            IdempotencyKey = Guard.MaxLength(Guard.NotNullOrWhiteSpace(idempotencyKey), 80),
            Direction = direction,
            Purpose = Guard.MaxLength(purpose, 15),
            MethodCode = Guard.MaxLength(methodCode, 15),
            ProviderCode = Guard.MaxLength(providerCode, 15),
            Amount = amount,
            Currency = Guard.MaxLength(currency, 3),
            CounterpartyPartyId = counterparty,
            BusinessRefType = businessRefType,
            BusinessRefId = businessRefId,
            BankAccountId = bankAccountId,
            InitiatedAt = now,
            Status = PaymentStatus.Initiated,
        };
        payment.MarkChanged();
        return payment;
    }

    public void AssignToBatch(Guid batchId)
    {
        PayoutBatchId = batchId;
        MarkChanged();
    }

    /// <summary>The payment was sent: the provider's immediate answer decides where it goes; no answer is Unknown (PAY-005).</summary>
    public Result Sent(ProviderOutcome outcome, string? externalRef, string? reason, DateOnly today, DateTimeOffset now)
    {
        var sent = Change(PaymentTrigger.SentToProvider, "PROVIDER", now);
        if (sent.IsFailure)
        {
            return sent;
        }

        ExternalRef = externalRef ?? ExternalRef;
        return outcome == ProviderOutcome.Pending ? Result.Success() : Outcome(outcome, "PROVIDER", reason, today, now);
    }

    /// <summary>
    /// Command RecordProviderCallback: a final status confirms or rejects a pending payment; an unmapped status makes it
    /// Unknown. A callback for a payment that is already final changes nothing (callbacks repeat).
    /// </summary>
    public Result RecordCallback(ProviderOutcome outcome, string? reason, DateOnly today, DateTimeOffset now)
    {
        if (Status != PaymentStatus.Pending || outcome == ProviderOutcome.Pending)
        {
            return Result.Success();
        }

        return Outcome(outcome, "CALLBACK", reason, today, now);
    }

    /// <summary>Command MarkUnknown: no answer within the time allowed.</summary>
    public Result MarkUnknown(DateTimeOffset now) => Outcome(ProviderOutcome.Unknown, "TIMEOUT", "No answer from the provider", DateOnly.FromDateTime(now.UtcDateTime), now);

    /// <summary>Command ResolveUnknown: a person decides from the evidence (a bank statement, the provider's report) (PAY-005).</summary>
    public Result ResolveUnknown(bool succeeded, string evidence, DateOnly today, DateTimeOffset now)
    {
        if (Status != PaymentStatus.Unknown)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Payment {PaymentRef} is {Status}, not Unknown.");
        }

        var result = Change(succeeded ? PaymentTrigger.ResolveUnknownSucceeded : PaymentTrigger.ResolveUnknownFailed, "OPERATOR", now);
        if (result.IsSuccess)
        {
            Announce(succeeded, $"Resolved by an operator: {evidence}", today, now);
        }

        return result;
    }

    /// <summary>Command AllocatePayment: a received payment applied to invoices, never above the amount received (BIL-003).</summary>
    public Result AllocatePayment(IReadOnlyList<(Guid InvoiceId, decimal Amount)> plan, string rule, DateTimeOffset now)
    {
        if (Direction != Direction.In || Status != PaymentStatus.Succeeded)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "Only a received payment that succeeded is allocated.");
        }

        if (plan.Any(p => p.Amount <= 0) || Allocated + plan.Sum(p => p.Amount) > Amount)
        {
            return Error.Validation(PaymentErrorCodes.OverAllocated, $"Allocations cannot exceed the {Amount} {Currency} received.");
        }

        foreach (var (invoiceId, amount) in plan)
        {
            var allocation = PaymentAllocation.Create(Id, invoiceId, amount, Currency, rule, now);
            _allocations.Add(allocation);
            Raise(new PaymentAllocated(Id, allocation.Id, invoiceId, amount, Currency, now));
        }

        MarkChanged();
        return Result.Success();
    }

    /// <summary>Command ReversePayment: a succeeded payment, and its allocations, are reversed.</summary>
    public Result ReversePayment(string reason, DateOnly today, DateTimeOffset now)
    {
        var result = Change(PaymentTrigger.ReversePayment, "OPERATOR", now);
        if (result.IsFailure)
        {
            return result;
        }

        // Each allocation is reversed by a reversing allocation row of the same amount, which it then points to (V0001's
        // fk_payment_allocation_reversed_by_id). Neither counts as allocated any more.
        var reversed = _allocations.Where(a => a.ReversedById is null && a.AllocationRule != ReversalRule).ToList();
        foreach (var allocation in reversed)
        {
            var reversal = PaymentAllocation.Create(Id, allocation.InvoiceId, allocation.Amount, Currency, ReversalRule, now);
            _allocations.Add(reversal);
            allocation.Reverse(reversal.Id);
        }

        Raise(new PaymentReversed(Id, PaymentRef, Direction, Purpose, Amount, Currency, today, reversed.Select(a => (a.Id, a.InvoiceId, a.Amount)).ToList(), reason, now));
        return result;
    }

    private Result Outcome(ProviderOutcome outcome, string source, string? reason, DateOnly today, DateTimeOffset now)
    {
        var trigger = outcome switch
        {
            ProviderOutcome.Succeeded => PaymentTrigger.ProviderConfirms,
            ProviderOutcome.Failed => PaymentTrigger.ProviderRejects,
            _ => PaymentTrigger.NoOrAmbiguousResponse,
        };
        var result = Change(trigger, source, now);
        if (result.IsFailure)
        {
            return result;
        }

        if (outcome == ProviderOutcome.Unknown)
        {
            Raise(new PaymentOutcomeUnknown(Id, PaymentRef, Direction, Purpose, Amount, Currency, now));
        }
        else
        {
            Announce(outcome == ProviderOutcome.Succeeded, reason ?? "Rejected by the provider", today, now);
        }

        return result;
    }

    private void Announce(bool succeeded, string reason, DateOnly today, DateTimeOffset now)
    {
        if (succeeded)
        {
            ValueDate ??= today;
            Raise(new PaymentConfirmed(Id, PaymentRef, Direction, Purpose, Amount, Currency, ValueDate.Value, CounterpartyPartyId, BusinessRefType, BusinessRefId, now));
        }
        else
        {
            Raise(new PaymentFailed(Id, PaymentRef, Direction, Purpose, Amount, Currency, BusinessRefType, BusinessRefId, reason, now));
        }
    }

    private Result Change(PaymentTrigger trigger, string source, DateTimeOffset now)
    {
        var next = Lifecycle.Fire(Status, trigger);
        if (next.IsFailure)
        {
            return next.Error;
        }

        _history.Add(StatusChange.Create(Id, Status, next.Value, source, now));
        Status = next.Value;
        MarkChanged();
        return Result.Success();
    }
}

public static class PaymentErrorCodes
{
    [ErrorCode(422, "error.payment.over_allocated")]
    public const string OverAllocated = "PAYMENT_OVER_ALLOCATED";

    [ErrorCode(422, "error.payment.no_provider")]
    public const string NoProvider = "PAYMENT_NO_PROVIDER";

    [ErrorCode(422, "error.payment.code_not_in_force")]
    public const string CodeNotInForce = "PAYMENT_CODE_NOT_IN_FORCE";

    [ErrorCode(401, "error.payment.callback_signature")]
    public const string CallbackSignatureInvalid = "CALLBACK_SIGNATURE_INVALID";

    [ErrorCode(409, "error.payment.batch_empty")]
    public const string BatchEmpty = "PAYOUT_BATCH_EMPTY";
}
