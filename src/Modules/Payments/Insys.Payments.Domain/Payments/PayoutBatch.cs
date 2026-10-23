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

public enum BatchStatus
{
    Built,
    Approved,
    Released,
    Settled,
}

public enum BatchTrigger
{
    ApproveBatch,
    ReleaseBatch,
    AllPaymentsFinal,
}

/// <summary>Asks Workflow for a second person's approval of the batch before release (SRS-002).</summary>
public sealed record PayoutBatchBuilt(Guid BatchId, string BatchRef, Guid BuiltBy, decimal TotalAmount, string Currency, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record PayoutReleased(Guid BatchId, string BatchRef, string Purpose, int PaymentCount, decimal TotalAmount, string Currency, DateTimeOffset OccurredAt)
    : IDomainEvent;

/// <summary>The PayoutBatch aggregate (payment.payout_batch): released only after approval by someone other than its builder (SRS-002).</summary>
public sealed class PayoutBatch : AggregateRoot<Guid>
{
    public static readonly StateMachine<BatchStatus, BatchTrigger> Lifecycle = new StateMachine<BatchStatus, BatchTrigger>()
        .Allow(BatchStatus.Built, BatchTrigger.ApproveBatch, BatchStatus.Approved)
        .Allow(BatchStatus.Approved, BatchTrigger.ReleaseBatch, BatchStatus.Released)
        .Allow(BatchStatus.Released, BatchTrigger.AllPaymentsFinal, BatchStatus.Settled);

    private PayoutBatch()
    {
    }

    public string BatchRef { get; private set; } = "";

    public string Purpose { get; private set; } = "";

    public int PaymentCount { get; private set; }

    public decimal TotalAmount { get; private set; }

    public string Currency { get; private set; } = "";

    public BatchStatus Status { get; private set; }

    public Guid? BuiltBy { get; private set; }

    public Guid? ApprovedBy { get; private set; }

    public Guid? ReleasedBy { get; private set; }

    /// <summary>Command BuildBatch from payments waiting to be paid out, all in one purpose and currency.</summary>
    public static Result<PayoutBatch> BuildBatch(Guid id, string purpose, string currency, IReadOnlyList<Payment> payments, Guid builtBy, DateTimeOffset now)
    {
        if (payments.Count == 0)
        {
            return Error.Conflict(PaymentErrorCodes.BatchEmpty, $"No {purpose} payments in {currency} are waiting for a batch.");
        }

        var batch = new PayoutBatch
        {
            Id = Guard.NotEmpty(id),
            BatchRef = "PB-" + id.ToString("N")[..16].ToUpperInvariant(),
            Purpose = purpose,
            Currency = currency,
            PaymentCount = payments.Count,
            TotalAmount = payments.Sum(p => p.Amount),
            Status = BatchStatus.Built,
            BuiltBy = builtBy,
        };
        foreach (var payment in payments)
        {
            payment.AssignToBatch(id);
        }

        batch.Raise(new PayoutBatchBuilt(id, batch.BatchRef, builtBy, batch.TotalAmount, currency, now));
        batch.MarkChanged();
        return batch;
    }

    /// <summary>Command ApproveBatch, from Workflow's ApprovalGranted; the approver is never the builder.</summary>
    public Result ApproveBatch(Guid approvedBy)
    {
        if (approvedBy == BuiltBy)
        {
            return MakerChecker.EnsureDifferentPeople(BuiltBy.ToString()!, approvedBy.ToString());
        }

        var result = Fire(BatchTrigger.ApproveBatch);
        if (result.IsSuccess)
        {
            ApprovedBy = approvedBy;
        }

        return result;
    }

    /// <summary>Command ReleaseBatch: the caller sends each payment to its provider.</summary>
    public Result ReleaseBatch(Guid releasedBy, DateTimeOffset now)
    {
        var result = Fire(BatchTrigger.ReleaseBatch);
        if (result.IsSuccess)
        {
            ReleasedBy = releasedBy;
            Raise(new PayoutReleased(Id, BatchRef, Purpose, PaymentCount, TotalAmount, Currency, now));
        }

        return result;
    }

    /// <summary>Transition "All payments final".</summary>
    public Result Settle(bool allPaymentsFinal) => Status == BatchStatus.Released && allPaymentsFinal ? Fire(BatchTrigger.AllPaymentsFinal) : Result.Success();

    private Result Fire(BatchTrigger trigger)
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

public enum ExceptionStatus
{
    Open,
    Investigating,
    Resolved,
}

public enum ExceptionTrigger
{
    AssignException,
    ResolveException,
}

public sealed record ReconciliationExceptionRaised(Guid ExceptionId, string ExceptionType, Guid? PaymentId, Guid? StatementLineId, decimal Amount, DateTimeOffset OccurredAt)
    : IDomainEvent;

/// <summary>
/// The ReconciliationException aggregate (payment.reconciliation_exception): every unmatched or ambiguous item is
/// investigated and resolved by a person (PAY-005).
/// </summary>
public sealed class ReconciliationException : AggregateRoot<Guid>
{
    public const string UnknownOutcome = "UNKNOWN_OUTCOME";
    public const string UnmatchedLine = "UNMATCHED_LINE";

    public static readonly StateMachine<ExceptionStatus, ExceptionTrigger> Lifecycle = new StateMachine<ExceptionStatus, ExceptionTrigger>()
        .Allow(ExceptionStatus.Open, ExceptionTrigger.AssignException, ExceptionStatus.Investigating)
        .Allow(ExceptionStatus.Investigating, ExceptionTrigger.ResolveException, ExceptionStatus.Resolved);

    private ReconciliationException()
    {
    }

    public string ExceptionType { get; private set; } = "";

    public Guid? StatementLineId { get; private set; }

    public Guid? PaymentId { get; private set; }

    public decimal Amount { get; private set; }

    public ExceptionStatus Status { get; private set; }

    public Guid? AssignedTo { get; private set; }

    public static ReconciliationException RaiseException(Guid id, string type, Guid? paymentId, Guid? statementLineId, decimal amount, DateTimeOffset now)
    {
        var exception = new ReconciliationException
        {
            Id = Guard.NotEmpty(id), ExceptionType = type, PaymentId = paymentId, StatementLineId = statementLineId, Amount = amount, Status = ExceptionStatus.Open,
        };
        exception.Raise(new ReconciliationExceptionRaised(id, type, paymentId, statementLineId, amount, now));
        exception.MarkChanged();
        return exception;
    }

    public Result AssignException(Guid assignee)
    {
        var next = Lifecycle.Fire(Status, ExceptionTrigger.AssignException);
        if (next.IsFailure)
        {
            return next.Error;
        }

        Status = next.Value;
        AssignedTo = assignee;
        MarkChanged();
        return Result.Success();
    }

    /// <summary>Command ResolveException, by the person investigating it.</summary>
    public Result ResolveException(Guid resolvedBy)
    {
        if (AssignedTo != resolvedBy)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "The exception is resolved by the person investigating it.");
        }

        var next = Lifecycle.Fire(Status, ExceptionTrigger.ResolveException);
        if (next.IsFailure)
        {
            return next.Error;
        }

        Status = next.Value;
        MarkChanged();
        return Result.Success();
    }
}

/// <summary>A line of an imported bank statement (payment.bank_statement_line), matched to a payment or raised as an exception (PAY-003).</summary>
public sealed class BankStatementLine : AggregateRoot<Guid>
{
    private BankStatementLine()
    {
    }

    public string AccountRef { get; private set; } = "";

    public DateOnly StatementDate { get; private set; }

    public decimal Amount { get; private set; }

    public string Currency { get; private set; } = "";

    public string Reference { get; private set; } = "";

    public Guid? MatchedPaymentId { get; private set; }

    /// <summary>UNMATCHED, MATCHED or EXCEPTION.</summary>
    public string MatchStatus { get; private set; } = "UNMATCHED";

    public static BankStatementLine Import(Guid id, string accountRef, DateOnly date, decimal amount, string currency, string reference)
    {
        var line = new BankStatementLine
        {
            Id = Guard.NotEmpty(id), AccountRef = Guard.MaxLength(accountRef, 15), StatementDate = date, Amount = amount, Currency = currency,
            Reference = Guard.MaxLength(reference, 80),
        };
        line.MarkChanged();
        return line;
    }

    public void Matched(Guid paymentId)
    {
        MatchedPaymentId = paymentId;
        MatchStatus = "MATCHED";
        MarkChanged();
    }

    public void Unmatched()
    {
        MatchStatus = "EXCEPTION";
        MarkChanged();
    }
}
