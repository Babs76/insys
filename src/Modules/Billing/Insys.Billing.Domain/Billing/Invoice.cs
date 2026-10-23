/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Billing
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

namespace Insys.Billing.Domain.Billing;

/// <summary>States from domain-model.json (billing › Invoice).</summary>
public enum InvoiceStatus
{
    Draft,
    Issued,
    PartPaid,
    Paid,
    Overdue,
    Credited,
}

public enum DocumentType
{
    Invoice,
    CreditNote,
}

/// <summary>An adjustment and reversal reason (billing.ref_adjustment_and_reversal_reason_codes).</summary>
public sealed record AdjustmentReason(string Code, bool RequiresApproval);

public sealed record InvoiceIssued(Guid InvoiceId, string InvoiceNumber, Guid BillingAccountId, Guid PayerPartyId, string PaymentMethod, string SourceDomain, Guid PolicyRef, string ProductVersionRef,
    DateOnly IssueDate, DateOnly DueDate, string Currency, decimal NetAmount, decimal TaxAmount, decimal TotalAmount, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record CreditNoteIssued(Guid CreditNoteId, string CreditNoteNumber, Guid InvoiceId, Guid BillingAccountId, string SourceDomain, Guid PolicyRef,
    DateOnly IssueDate, string Currency, decimal TotalAmount, string ReasonCode, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record InvoiceSettled(Guid InvoiceId, Guid BillingAccountId, string SourceDomain, Guid PolicyRef, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>Entity InvoiceLine (billing.invoice_line).</summary>
public sealed class InvoiceLine : Entity<Guid>, ITenantScoped
{
    private InvoiceLine()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid InvoiceId { get; private set; }

    public short LineNo { get; private set; }

    public string ChargeType { get; private set; } = "";

    public string Description { get; private set; } = "";

    public decimal Amount { get; private set; }

    public string? TaxCode { get; private set; }

    public long RowVersion { get; private set; }

    internal static InvoiceLine Create(Guid invoiceId, short lineNo, string chargeType, string description, decimal amount) =>
        new() { Id = Guid.CreateVersion7(), InvoiceId = invoiceId, LineNo = lineNo, ChargeType = chargeType, Description = description, Amount = amount };
}

/// <summary>Entity Adjustment (billing.adjustment): the auditable record of a credit against an invoice (BIL-004).</summary>
public sealed class Adjustment : Entity<Guid>, ITenantScoped
{
    private Adjustment()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid InvoiceId { get; private set; }

    public string ReasonCode { get; private set; } = "";

    public decimal Amount { get; private set; }

    public string Currency { get; private set; } = "";

    public Guid? CreditNoteId { get; private set; }

    /// <summary>Who authorised it; Guid.Empty for a credit raised by a domain event, such as a policy cancellation (OQ-058).</summary>
    public Guid ApprovedBy { get; private set; }

    public DateTimeOffset AdjustedAt { get; private set; }

    internal static Adjustment Create(Guid invoiceId, string reasonCode, decimal amount, string currency, Guid creditNoteId, Guid approvedBy, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(), InvoiceId = invoiceId, ReasonCode = reasonCode, Amount = amount, Currency = currency, CreditNoteId = creditNoteId,
        ApprovedBy = approvedBy, AdjustedAt = now,
    };
}

/// <summary>
/// The Invoice aggregate (billing.invoice), an invoice or a credit note. It references its source policy, tenant,
/// product version, currency and dates (BIL-002); it never changes once issued, and a correction is a credit note
/// (BIL-004); its settled status reflects the payments allocated to it (BIL-003).
/// </summary>
public sealed class Invoice : AggregateRoot<Guid>
{
    private readonly List<InvoiceLine> _lines = [];
    private readonly List<Adjustment> _adjustments = [];

    private Invoice()
    {
    }

    public string InvoiceNumber { get; private set; } = "";

    public DocumentType DocumentType { get; private set; }

    public Guid BillingAccountId { get; private set; }

    public string SourceDomain { get; private set; } = "";

    public Guid PolicyRef { get; private set; }

    public string ProductVersionRef { get; private set; } = "";

    public DateOnly IssueDate { get; private set; }

    public DateOnly DueDate { get; private set; }

    public string Currency { get; private set; } = "";

    public decimal NetAmount { get; private set; }

    public decimal TaxAmount { get; private set; }

    public decimal TotalAmount { get; private set; }

    public decimal AllocatedAmount { get; private set; }

    public InvoiceStatus Status { get; private set; }

    /// <summary>For a credit note: the invoice it corrects.</summary>
    public Guid? ReversesInvoiceId { get; private set; }

    public IReadOnlyList<InvoiceLine> Lines => _lines;

    public IReadOnlyList<Adjustment> Adjustments => _adjustments;

    public decimal Outstanding => DocumentType == DocumentType.Invoice && Status != InvoiceStatus.Credited ? TotalAmount - AllocatedAmount - Credited : 0;

    /// <summary>Credit notes raised against this invoice so far.</summary>
    public decimal Credited => _adjustments.Sum(a => a.Amount);

    /// <summary>Command IssueInvoice: for the instalments due, with one line each. TODO(spec): premium taxes on invoices are not specified (OQ-058).</summary>
    public static Result<Invoice> IssueInvoice(Guid id, string prefix, BillingAccount account, PremiumSchedule schedule, IReadOnlyList<Instalment> instalments,
        string paymentMethod, DateOnly issueDate, DateTimeOffset now)
    {
        if (instalments.Count == 0 || account.Status == AccountStatus.Closed)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "An invoice bills at least one instalment on an open account.");
        }

        var invoice = New(id, prefix, DocumentType.Invoice, account.Id, schedule.SourceDomain, schedule.PolicyRef, schedule.ProductVersionRef, issueDate,
            instalments.Min(i => i.DueDate) < issueDate ? issueDate : instalments.Min(i => i.DueDate), account.Currency, null);
        foreach (var instalment in instalments.OrderBy(i => i.DueDate))
        {
            invoice._lines.Add(InvoiceLine.Create(id, (short)(invoice._lines.Count + 1), "PREMIUM", $"Premium due {instalment.DueDate:yyyy-MM-dd}", instalment.Amount));
        }

        invoice.NetAmount = invoice._lines.Sum(l => l.Amount);
        invoice.TotalAmount = invoice.NetAmount;
        invoice.Status = InvoiceStatus.Issued;
        invoice.Raise(new InvoiceIssued(id, invoice.InvoiceNumber, account.Id, account.PayerPartyId, paymentMethod, invoice.SourceDomain, invoice.PolicyRef, invoice.ProductVersionRef, issueDate,
            invoice.DueDate, invoice.Currency, invoice.NetAmount, invoice.TaxAmount, invoice.TotalAmount, now));
        return invoice;
    }

    /// <summary>
    /// Command RaiseCreditNote (BIL-004): a credit note for part or all of what remains on this invoice. The invoice is not
    /// changed except to record the adjustment; fully credited, it is Credited.
    /// </summary>
    public Result<Invoice> RaiseCreditNote(Guid creditNoteId, string prefix, decimal amount, AdjustmentReason reason, Guid approvedBy, DateOnly issueDate,
        DateTimeOffset now)
    {
        if (DocumentType != DocumentType.Invoice || Status is InvoiceStatus.Draft or InvoiceStatus.Credited)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "Only an issued invoice is credited.");
        }

        if (amount <= 0 || amount > TotalAmount - Credited)
        {
            return Error.Validation(BillingErrorCodes.CreditExceedsInvoice, $"A credit note is positive and at most {TotalAmount - Credited} {Currency}.");
        }

        var credit = New(creditNoteId, prefix, DocumentType.CreditNote, BillingAccountId, SourceDomain, PolicyRef, ProductVersionRef, issueDate, issueDate, Currency, Id);
        credit._lines.Add(InvoiceLine.Create(creditNoteId, 1, "CREDIT", $"Credit for {InvoiceNumber} ({reason.Code})", amount));
        credit.NetAmount = amount;
        credit.TotalAmount = amount;
        credit.Status = InvoiceStatus.Issued;
        credit.Raise(new CreditNoteIssued(creditNoteId, credit.InvoiceNumber, Id, BillingAccountId, SourceDomain, PolicyRef, issueDate, Currency, amount, reason.Code, now));

        _adjustments.Add(Adjustment.Create(Id, reason.Code, amount, Currency, creditNoteId, approvedBy, now));
        if (Credited + AllocatedAmount >= TotalAmount)
        {
            Status = InvoiceStatus.Credited;
        }

        MarkChanged();
        return credit;
    }

    /// <summary>Command MarkSettled: an allocation from Payments (BIL-003). Returns the amount applied; anything above what is outstanding is not applied.</summary>
    public decimal ApplyAllocation(decimal amount, DateTimeOffset now)
    {
        var applied = Math.Min(amount, Math.Max(0, TotalAmount - AllocatedAmount - Credited));
        if (applied <= 0 || DocumentType != DocumentType.Invoice)
        {
            return 0;
        }

        AllocatedAmount += applied;
        var settled = AllocatedAmount + Credited >= TotalAmount;
        Status = settled ? InvoiceStatus.Paid : Status == InvoiceStatus.Overdue ? InvoiceStatus.Overdue : InvoiceStatus.PartPaid;
        if (settled)
        {
            Raise(new InvoiceSettled(Id, BillingAccountId, SourceDomain, PolicyRef, now));
        }

        MarkChanged();
        return applied;
    }

    /// <summary>A reversed payment's allocation is taken back: the invoice reopens (domain-model.json: PaymentReversed → reopen invoice).</summary>
    public void ReverseAllocation(decimal amount, DateOnly today)
    {
        AllocatedAmount = Math.Max(0, AllocatedAmount - amount);
        if (Status is InvoiceStatus.Paid or InvoiceStatus.PartPaid or InvoiceStatus.Overdue)
        {
            Status = AllocatedAmount > 0 ? InvoiceStatus.PartPaid : DueDate < today ? InvoiceStatus.Overdue : InvoiceStatus.Issued;
        }

        MarkChanged();
    }

    /// <summary>Transition "Due date passed": an issued invoice not paid by its due date.</summary>
    public bool MarkOverdue(DateOnly today)
    {
        if (Status != InvoiceStatus.Issued || DueDate >= today)
        {
            return false;
        }

        Status = InvoiceStatus.Overdue;
        MarkChanged();
        return true;
    }

    private static Invoice New(Guid id, string prefix, DocumentType type, Guid accountId, string sourceDomain, Guid policyRef, string productVersionRef, DateOnly issueDate,
        DateOnly dueDate, string currency, Guid? reverses)
    {
        var invoice = new Invoice
        {
            Id = Guard.NotEmpty(id),
            InvoiceNumber = prefix.Length <= 8 ? References.Of(prefix, id, prefix.Length + 12) : References.Of("", id, 15),
            DocumentType = type,
            BillingAccountId = accountId,
            SourceDomain = sourceDomain,
            PolicyRef = policyRef,
            ProductVersionRef = productVersionRef,
            IssueDate = issueDate,
            DueDate = dueDate,
            Currency = currency,
            ReversesInvoiceId = reverses,
            Status = InvoiceStatus.Draft,
        };
        invoice.MarkChanged();
        return invoice;
    }
}

public enum RefundStatus
{
    Requested,
    Approved,
    Rejected,
    Paid,
}

public enum RefundTrigger
{
    ApproveRefund,
    RejectRefund,
    PaymentSucceeded,
}

/// <summary>Asks Workflow for a second person's approval of the refund (SRS-002).</summary>
public sealed record RefundRequested(Guid RefundId, Guid RequestedBy, decimal Amount, string Currency, string ReasonCode, string SubjectHash, DateTimeOffset OccurredAt)
    : IDomainEvent;

public sealed record RefundApproved(Guid RefundId, Guid BillingAccountId, Guid PayerPartyId, Guid BankAccountId, decimal Amount, string Currency, DateTimeOffset OccurredAt)
    : IDomainEvent;

/// <summary>The RefundRequest aggregate (billing.refund_request): approved by a second person before any payment is instructed (SRS-002).</summary>
public sealed class RefundRequest : AggregateRoot<Guid>
{
    public static readonly StateMachine<RefundStatus, RefundTrigger> Lifecycle = new StateMachine<RefundStatus, RefundTrigger>()
        .Allow(RefundStatus.Requested, RefundTrigger.ApproveRefund, RefundStatus.Approved)
        .Allow(RefundStatus.Requested, RefundTrigger.RejectRefund, RefundStatus.Rejected)
        .Allow(RefundStatus.Approved, RefundTrigger.PaymentSucceeded, RefundStatus.Paid);

    private RefundRequest()
    {
    }

    public Guid BillingAccountId { get; private set; }

    public decimal Amount { get; private set; }

    public string Currency { get; private set; } = "";

    public string ReasonCode { get; private set; } = "";

    public RefundStatus Status { get; private set; }

    public Guid? PaymentId { get; private set; }

    public Guid? RequestedBy { get; private set; }

    public Guid? DecidedBy { get; private set; }

    public Guid PayerPartyId { get; private set; }

    /// <summary>The payer's bank account (party.bank_account) the refund is paid to, and the keyed hash of its number, as approved.</summary>
    public Guid? BankAccountId { get; private set; }

    public string? PayeeBankHash { get; private set; }

    /// <summary>What the refund's approval covers: payer, bank account, amount and currency.</summary>
    public string SubjectHash => ApprovalSubject.Hash("REFUND", Id, PayerPartyId, BankAccountId, PayeeBankHash, Amount, Currency);

    /// <summary>Command RequestRefund, to the payer's verified bank account.</summary>
    public static Result<RefundRequest> RequestRefund(Guid id, BillingAccount account, decimal amount, string reasonCode, Guid bankAccountId, string bankHash,
        Guid requestedBy, DateTimeOffset now)
    {
        if (amount <= 0 || decimal.Round(amount, 2) != amount)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "A refund is a positive amount to two decimal places.");
        }

        var refund = new RefundRequest
        {
            Id = Guard.NotEmpty(id), BillingAccountId = account.Id, Amount = amount, Currency = account.Currency, ReasonCode = reasonCode,
            Status = RefundStatus.Requested, RequestedBy = requestedBy, PayerPartyId = account.PayerPartyId, BankAccountId = Guard.NotEmpty(bankAccountId),
            PayeeBankHash = bankHash,
        };
        refund.Raise(new RefundRequested(id, requestedBy, amount, account.Currency, reasonCode, refund.SubjectHash, now));
        refund.MarkChanged();
        return refund;
    }

    /// <summary>Workflow's decision (ApproveRefund or RejectRefund). The checker is never the requester.</summary>
    public Result Decide(bool approved, Guid checkerId, string? subjectHash, DateTimeOffset now)
    {
        if (subjectHash != SubjectHash)
        {
            return Error.Conflict(BillingErrorCodes.ApprovalSubjectChanged, "The approval was for another payee account or amount.");
        }

        if (checkerId == RequestedBy)
        {
            return MakerChecker.EnsureDifferentPeople(RequestedBy.ToString()!, checkerId.ToString());
        }

        var result = Fire(approved ? RefundTrigger.ApproveRefund : RefundTrigger.RejectRefund);
        if (result.IsSuccess)
        {
            DecidedBy = checkerId;
            if (approved)
            {
                Raise(new RefundApproved(Id, BillingAccountId, PayerPartyId, BankAccountId!.Value, Amount, Currency, now));
            }
        }

        return result;
    }

    /// <summary>The payout was created for this refund (GAP-036): its ID is known before it completes. No status change.</summary>
    public void LinkPayment(Guid paymentId)
    {
        if (PaymentId is null && Status == RefundStatus.Approved)
        {
            PaymentId = paymentId;
            MarkChanged();
        }
    }

    /// <summary>Transition "Payment succeeded".</summary>
    public Result MarkPaid(Guid paymentId)
    {
        var result = Fire(RefundTrigger.PaymentSucceeded);
        if (result.IsSuccess)
        {
            PaymentId = paymentId;
        }

        return result;
    }

    private Result Fire(RefundTrigger trigger)
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
