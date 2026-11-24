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
    DateOnly IssueDate, DateOnly DueDate, string Currency, decimal NetAmount, decimal TaxAmount, decimal TotalAmount, DateTimeOffset OccurredAt, string? TaxCode = null) : IDomainEvent;

/// <param name="InvoiceOutstanding">What the invoice still asks for after this credit (DE-009).</param>
public sealed record CreditNoteIssued(Guid CreditNoteId, string CreditNoteNumber, Guid InvoiceId, Guid BillingAccountId, string SourceDomain, Guid PolicyRef,
    DateOnly IssueDate, string Currency, decimal TotalAmount, string ReasonCode, DateTimeOffset OccurredAt, decimal TaxAmount = 0, string? TaxCode = null,
    decimal? InvoiceOutstanding = null) : IDomainEvent;

/// <summary>DE-010 (OQ-058, Ade, 24 November 2026): asks Workflow for a CREDIT_NOTE approval of a credit whose reason requires one.</summary>
public sealed record CreditNoteApprovalRequested(Guid RequestId, Guid InvoiceId, string InvoiceNumber, Guid RequestedBy, decimal Amount, string Currency, string ReasonCode,
    string SubjectHash, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record InvoiceSettled(Guid InvoiceId, Guid BillingAccountId, string SourceDomain, Guid PolicyRef, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record InvoiceOverdue(Guid InvoiceId, Guid BillingAccountId, string SourceDomain, Guid PolicyRef, DateOnly DueDate, decimal Outstanding, string Currency,
    DateTimeOffset OccurredAt) : IDomainEvent;

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

    /// <summary>The tax included in <see cref="Amount"/> (OQ-058).</summary>
    public decimal TaxAmount { get; private set; }

    public long RowVersion { get; private set; }

    internal static InvoiceLine Create(Guid invoiceId, short lineNo, string chargeType, string description, decimal amount, string? taxCode = null, decimal taxAmount = 0) =>
        new() { Id = Guid.CreateVersion7(), InvoiceId = invoiceId, LineNo = lineNo, ChargeType = chargeType, Description = description, Amount = amount, TaxCode = taxCode, TaxAmount = taxAmount };
}

/// <summary>The tax a product's invoices carry, by its rate in force on the issue date (OQ-156). The rate is a percentage.</summary>
public sealed record InvoiceTax(string TaxCode, decimal Rate)
{
    /// <summary>The tax included in an amount: amount x rate / (100 + rate), to the cent.</summary>
    public decimal IncludedIn(decimal amount) => Math.Round(amount * Rate / (100m + Rate), 2, MidpointRounding.AwayFromZero);
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

public enum CreditNoteRequestStatus
{
    /// <summary>Waiting for its CREDIT_NOTE approval; the invoice goes on taking allocations meanwhile.</summary>
    Pending,

    /// <summary>Approved, and its credit note issued for what was still outstanding (at most the amount asked).</summary>
    Issued,

    /// <summary>Approved when nothing was outstanding any more (paid or credited meanwhile): no credit note is issued.</summary>
    Lapsed,

    /// <summary>The approval ended without being granted (rejected, expired or withdrawn): nothing is credited.</summary>
    Rejected,
}

/// <summary>
/// Entity CreditNoteRequest (billing.credit_note_request, V0150): a credit note whose adjustment reason requires approval, waiting for a second
/// person (DE-010, OQ-058, Ade, 24 November 2026). It belongs to the invoice it would credit, so its grant changes that one aggregate.
/// </summary>
public sealed class CreditNoteRequest : Entity<Guid>, ITenantScoped
{
    private CreditNoteRequest()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid InvoiceId { get; private set; }

    /// <summary>The amount asked for; what is credited is at most what the invoice still asks for when the approval is granted.</summary>
    public decimal Amount { get; private set; }

    public string Currency { get; private set; } = "";

    public string ReasonCode { get; private set; } = "";

    public CreditNoteRequestStatus Status { get; private set; }

    public Guid RequestedBy { get; private set; }

    public DateTimeOffset RequestedAt { get; private set; }

    /// <summary>The checker who decided it; null for an approval that expired or was withdrawn.</summary>
    public Guid? DecidedBy { get; private set; }

    public Guid? ApprovalId { get; private set; }

    /// <summary>How a refusal ended: REJECTED, EXPIRED or WITHDRAWN (Workflow's endings).</summary>
    public string? Ending { get; private set; }

    public Guid? CreditNoteId { get; private set; }

    /// <summary>What the credit note issued on approval credited; 0 when it lapsed.</summary>
    public decimal? CreditedAmount { get; private set; }

    public long RowVersion { get; private set; }

    /// <summary>What the CREDIT_NOTE approval covers: the invoice, the amount, its currency and the reason.</summary>
    public string SubjectHash => ApprovalSubject.Hash("CREDIT_NOTE", Id, InvoiceId, Amount, Currency, ReasonCode);

    internal static CreditNoteRequest Create(Guid id, Guid invoiceId, decimal amount, string currency, string reasonCode, Guid requestedBy, DateTimeOffset now) => new()
    {
        Id = Guard.NotEmpty(id), InvoiceId = invoiceId, Amount = amount, Currency = currency, ReasonCode = reasonCode, Status = CreditNoteRequestStatus.Pending,
        RequestedBy = requestedBy, RequestedAt = now,
    };

    internal void Issue(Guid checker, Guid? approvalId, Guid creditNoteId, decimal credited) =>
        (Status, DecidedBy, ApprovalId, CreditNoteId, CreditedAmount) = (CreditNoteRequestStatus.Issued, checker, approvalId, creditNoteId, credited);

    internal void Lapse(Guid checker, Guid? approvalId) => (Status, DecidedBy, ApprovalId, CreditedAmount) = (CreditNoteRequestStatus.Lapsed, checker, approvalId, 0m);

    internal void Reject(Guid? checker, Guid? approvalId, string ending) =>
        (Status, DecidedBy, ApprovalId, Ending) = (CreditNoteRequestStatus.Rejected, checker, approvalId, Guard.MaxLength(ending, 12));
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
    private readonly List<CreditNoteRequest> _creditNoteRequests = [];

    private Invoice()
    {
    }

    public string InvoiceNumber { get; private set; } = "";

    public DocumentType DocumentType { get; private set; }

    public Guid BillingAccountId { get; private set; }

    public string SourceDomain { get; private set; } = "";

    public Guid PolicyRef { get; private set; }

    public string ProductVersionRef { get; private set; } = "";

    /// <summary>The schedule's product and organisation node (F103); a credit note keeps its invoice's.</summary>
    public string? ProductCode { get; private set; }

    public string? OrgNodeCode { get; private set; }

    public DateOnly IssueDate { get; private set; }

    public DateOnly DueDate { get; private set; }

    public string Currency { get; private set; } = "";

    public decimal NetAmount { get; private set; }

    public decimal TaxAmount { get; private set; }

    /// <summary>How much of <see cref="TaxAmount"/> credit notes have taken back.</summary>
    public decimal CreditedTax { get; private set; }

    public decimal TotalAmount { get; private set; }

    public decimal AllocatedAmount { get; private set; }

    public InvoiceStatus Status { get; private set; }

    /// <summary>For a credit note: the invoice it corrects.</summary>
    public Guid? ReversesInvoiceId { get; private set; }

    public IReadOnlyList<InvoiceLine> Lines => _lines;

    public IReadOnlyList<Adjustment> Adjustments => _adjustments;

    /// <summary>Credit notes asked for with a reason that requires approval (DE-010), pending or decided.</summary>
    public IReadOnlyList<CreditNoteRequest> CreditNoteRequests => _creditNoteRequests;

    public decimal Outstanding => DocumentType == DocumentType.Invoice && Status != InvoiceStatus.Credited ? TotalAmount - AllocatedAmount - Credited : 0;

    /// <summary>Credit notes raised against this invoice so far.</summary>
    public decimal Credited => _adjustments.Sum(a => a.Amount);

    /// <summary>
    /// Command IssueInvoice: for the instalments due, with one line each. The invoice is due on its earliest instalment's
    /// due date, so a premium invoiced a day late is still overdue from the day it fell due (request 46), but never before
    /// <paramref name="dueNoEarlierThan"/>: back premiums of a policy billed for the first time (backdated, reinstated,
    /// enrolled late) are due from the day they are invoiced. With <paramref name="tax"/>, each line's tax is taken out of its amount (OQ-058): the premium
    /// already includes it, so the total is the same with or without it.
    /// </summary>
    public string? MigrationBatch { get; private set; }

    public string? SourceReference { get; private set; }

    /// <summary>B-013: a premium outstanding in the source system. It was invoiced and posted there, so nothing is raised: the opening balances carry it in the ledger.</summary>
    public static Invoice Migrated(Guid id, string prefix, BillingAccount account, PremiumSchedule schedule, decimal amount, DateOnly dueDate, DateOnly issueDate, string batch, string sourceReference)
    {
        var invoice = New(id, prefix, DocumentType.Invoice, account.Id, schedule.SourceDomain, schedule.PolicyRef, schedule.ProductVersionRef, issueDate, dueDate, account.Currency, null);
        (invoice.ProductCode, invoice.OrgNodeCode) = (schedule.ProductCode, schedule.OrgNodeCode);
        invoice._lines.Add(InvoiceLine.Create(id, 1, "PREMIUM", $"Premium outstanding from before migration, due {dueDate:yyyy-MM-dd}", amount, null, 0));
        invoice.TotalAmount = amount;
        invoice.NetAmount = amount;
        invoice.Status = InvoiceStatus.Issued;
        invoice.MigrationBatch = Guard.MaxLength(batch, 30);
        invoice.SourceReference = Guard.MaxLength(sourceReference, 40);
        invoice.MarkChanged();
        return invoice;
    }

    public static Result<Invoice> IssueInvoice(Guid id, string prefix, BillingAccount account, PremiumSchedule schedule, IReadOnlyList<Instalment> instalments,
        string paymentMethod, DateOnly issueDate, DateTimeOffset now, DateOnly? dueNoEarlierThan = null, InvoiceTax? tax = null)
    {
        if (instalments.Count == 0 || account.Status == AccountStatus.Closed)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "An invoice bills at least one instalment on an open account.");
        }

        var invoice = New(id, prefix, DocumentType.Invoice, account.Id, schedule.SourceDomain, schedule.PolicyRef, schedule.ProductVersionRef, issueDate,
            dueNoEarlierThan is { } earliest && instalments.Min(i => i.DueDate) < earliest ? earliest : instalments.Min(i => i.DueDate), account.Currency, null);
        (invoice.ProductCode, invoice.OrgNodeCode) = (schedule.ProductCode, schedule.OrgNodeCode);
        foreach (var instalment in instalments.OrderBy(i => i.DueDate))
        {
            invoice._lines.Add(InvoiceLine.Create(id, (short)(invoice._lines.Count + 1), "PREMIUM", $"Premium due {instalment.DueDate:yyyy-MM-dd}", instalment.Amount, tax?.TaxCode,
                tax?.IncludedIn(instalment.Amount) ?? 0));
        }

        invoice.TotalAmount = invoice._lines.Sum(l => l.Amount);
        invoice.TaxAmount = invoice._lines.Sum(l => l.TaxAmount);
        invoice.NetAmount = invoice.TotalAmount - invoice.TaxAmount;
        invoice.Status = InvoiceStatus.Issued;
        invoice.Raise(new InvoiceIssued(id, invoice.InvoiceNumber, account.Id, account.PayerPartyId, paymentMethod, invoice.SourceDomain, invoice.PolicyRef, invoice.ProductVersionRef, issueDate,
            invoice.DueDate, invoice.Currency, invoice.NetAmount, invoice.TaxAmount, invoice.TotalAmount, now, tax?.TaxCode));
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
        (credit.ProductCode, credit.OrgNodeCode) = (ProductCode, OrgNodeCode);
        // The credit's share of the invoice's tax; the last credit takes what remains, so credits add up to the invoice's tax exactly.
        var fullyCredited = Credited + amount >= TotalAmount;
        var creditTax = fullyCredited ? TaxAmount - CreditedTax : Math.Min(TaxAmount - CreditedTax, Math.Round(TaxAmount * amount / TotalAmount, 2, MidpointRounding.AwayFromZero));
        var taxCode = _lines.Select(l => l.TaxCode).FirstOrDefault(c => c is not null);
        CreditedTax += creditTax;
        credit._lines.Add(InvoiceLine.Create(creditNoteId, 1, "CREDIT", $"Credit for {InvoiceNumber} ({reason.Code})", amount, taxCode, creditTax));
        credit.TaxAmount = creditTax;
        credit.NetAmount = amount - creditTax;
        credit.TotalAmount = amount;
        credit.Status = InvoiceStatus.Issued;

        _adjustments.Add(Adjustment.Create(Id, reason.Code, amount, Currency, creditNoteId, approvedBy, now));
        if (Credited + AllocatedAmount >= TotalAmount)
        {
            Status = InvoiceStatus.Credited;
        }

        // DE-009: the event says what the invoice still asks for after this credit, so a consumer knows whether it was fully credited.
        credit.Raise(new CreditNoteIssued(creditNoteId, credit.InvoiceNumber, Id, BillingAccountId, SourceDomain, PolicyRef, issueDate, Currency, amount, reason.Code, now, creditTax,
            taxCode, Outstanding));
        MarkChanged();
        return credit;
    }

    /// <summary>
    /// DE-010 (OQ-058, Ade, 24 November 2026): a credit note whose reason requires approval is asked for, not issued. It waits for a
    /// CREDIT_NOTE approval, decided by a second person within their authority (Workflow); the invoice is not frozen meanwhile. At most
    /// what the invoice asks for now may be asked for.
    /// </summary>
    public Result<CreditNoteRequest> RequestCreditNote(Guid requestId, decimal amount, AdjustmentReason reason, Guid requestedBy, DateTimeOffset now)
    {
        if (DocumentType != DocumentType.Invoice || Status is InvoiceStatus.Draft or InvoiceStatus.Credited)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "Only an issued invoice is credited.");
        }

        if (amount <= 0 || decimal.Round(amount, 2) != amount || amount > Outstanding)
        {
            return Error.Validation(BillingErrorCodes.CreditExceedsInvoice, $"A credit note that needs approval is positive and at most what the invoice still asks for, {Outstanding} {Currency}.");
        }

        var request = CreditNoteRequest.Create(requestId, Id, amount, Currency, reason.Code, requestedBy, now);
        _creditNoteRequests.Add(request);
        Raise(new CreditNoteApprovalRequested(request.Id, Id, InvoiceNumber, requestedBy, amount, Currency, reason.Code, request.SubjectHash, now));
        MarkChanged();
        return request;
    }

    /// <summary>
    /// Workflow's decision on a credit note request (DE-010). Granted, the credit note is issued for what the invoice still asks for, never
    /// more than was asked: allocations went on while it waited. If nothing is outstanding any more (paid or credited meanwhile), the request
    /// lapses and no credit note is issued: a credit then would be money owed back to the payer, which is a refund's, not a credit note's.
    /// Any other ending (rejected, expired, withdrawn) clears the request and credits nothing. The checker is never the requester; a
    /// redelivered decision changes nothing. Returns the credit note issued, if one was.
    /// </summary>
    public Result<Invoice?> DecideCreditNote(Guid requestId, bool approved, Guid checkerId, string? subjectHash, Guid? approvalId, string ending, Guid creditNoteId,
        string prefix, DateOnly issueDate, DateTimeOffset now)
    {
        if (_creditNoteRequests.SingleOrDefault(r => r.Id == requestId) is not { } request)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The invoice has no such credit note request.");
        }

        if (request.Status != CreditNoteRequestStatus.Pending)
        {
            var redelivered = approved
                ? request.Status is CreditNoteRequestStatus.Issued or CreditNoteRequestStatus.Lapsed && request.ApprovalId == approvalId
                : request.Status == CreditNoteRequestStatus.Rejected;
            return redelivered ? Result<Invoice?>.Success(null) : Error.Conflict(ErrorCodes.InvalidStateTransition, "The credit note request was already decided otherwise.");
        }

        if (subjectHash != request.SubjectHash)
        {
            return Error.Conflict(BillingErrorCodes.ApprovalSubjectChanged, "The approval was for another credit note.");
        }

        var hasChecker = approved || checkerId != Guid.Empty;
        if (hasChecker && checkerId == request.RequestedBy)
        {
            return MakerChecker.EnsureDifferentPeople(request.RequestedBy.ToString(), checkerId.ToString()).Error!;
        }

        if (!approved)
        {
            request.Reject(hasChecker ? checkerId : null, approvalId, ending);
            MarkChanged();
            return Result<Invoice?>.Success(null);
        }

        var credit = Math.Min(request.Amount, Outstanding);
        if (credit <= 0)
        {
            request.Lapse(checkerId, approvalId);
            MarkChanged();
            return Result<Invoice?>.Success(null);
        }

        var issued = RaiseCreditNote(creditNoteId, prefix, credit, new AdjustmentReason(request.ReasonCode, true), checkerId, issueDate, now);
        if (issued.IsFailure)
        {
            return issued.Error!;
        }

        request.Issue(checkerId, approvalId, creditNoteId, credit);
        return issued.Value;
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
    /// <summary>Transition "Due date passed": an issued invoice not paid by its due date becomes overdue (InvoiceOverdue).</summary>
    public bool MarkOverdue(DateOnly today, DateTimeOffset now)
    {
        if (Status != InvoiceStatus.Issued || DueDate >= today)
        {
            return false;
        }

        Status = InvoiceStatus.Overdue;
        MarkChanged();
        Raise(new InvoiceOverdue(Id, BillingAccountId, SourceDomain, PolicyRef, DueDate, Outstanding, Currency, now));
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

    /// <summary>DE-008: its payout failed; it waits for a retry (PAYOUT_RETRY, a second person) or to be cancelled.</summary>
    PaymentFailed,

    /// <summary>DE-008: cancelled after its payout failed; the money stays with the account as credit and nothing is paid.</summary>
    Cancelled,
}

public enum RefundTrigger
{
    ApproveRefund,
    RejectRefund,
    PaymentSucceeded,
    PaymentFailed,
    RetryGranted,
    CancelRefund,
}

/// <summary>Asks Workflow for a second person's approval of the refund (SRS-002).</summary>
public sealed record RefundRequested(Guid RefundId, Guid RequestedBy, decimal Amount, string Currency, string ReasonCode, string SubjectHash, DateTimeOffset OccurredAt)
    : IDomainEvent;

public sealed record RefundApproved(Guid RefundId, Guid BillingAccountId, Guid PayerPartyId, Guid BankAccountId, decimal Amount, string Currency, DateTimeOffset OccurredAt)
    : IDomainEvent;

/// <summary>DE-008: asks Workflow for a PAYOUT_RETRY approval of paying the refund again (SRS-002, PAY-005).</summary>
public sealed record RefundPayoutRetryRequested(Guid RefundId, Guid RequestedBy, int Attempt, decimal Amount, string Currency, string FailedPaymentRef, string SubjectHash,
    DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>DE-008: the retry was granted; Payments makes a new payout, which waits for a payout batch (payment.PayoutRequested).</summary>
public sealed record RefundPayoutRetryGranted(Guid RefundId, int Attempt, decimal Amount, string Currency, Guid PayerPartyId, Guid BankAccountId, DateTimeOffset OccurredAt)
    : IDomainEvent;

/// <summary>DE-008: the refund was cancelled after its payout failed; it will not be paid (payment.PayoutAbandoned).</summary>
public sealed record RefundCancelled(Guid RefundId, string Reason, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>The RefundRequest aggregate (billing.refund_request): approved by a second person before any payment is instructed (SRS-002).</summary>
public sealed class RefundRequest : AggregateRoot<Guid>
{
    public static readonly StateMachine<RefundStatus, RefundTrigger> Lifecycle = new StateMachine<RefundStatus, RefundTrigger>()
        .Allow(RefundStatus.Requested, RefundTrigger.ApproveRefund, RefundStatus.Approved)
        .Allow(RefundStatus.Requested, RefundTrigger.RejectRefund, RefundStatus.Rejected)
        .Allow(RefundStatus.Approved, RefundTrigger.PaymentSucceeded, RefundStatus.Paid)
        // DE-008 (Ade, 24 November 2026): a failed payout is paid again only on a second person's approval, or the refund is cancelled.
        .Allow(RefundStatus.Approved, RefundTrigger.PaymentFailed, RefundStatus.PaymentFailed)
        .Allow(RefundStatus.PaymentFailed, RefundTrigger.RetryGranted, RefundStatus.Approved)
        .Allow(RefundStatus.PaymentFailed, RefundTrigger.CancelRefund, RefundStatus.Cancelled);

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

    /// <summary>The Workflow approval that approved the refund (BR-055); null until approved.</summary>
    public Guid? ApprovalId { get; private set; }

    public string? PayeeBankHash { get; private set; }

    /// <summary>The payout attempt the refund is on: 1 for its first payout, then one more for each retry granted (DE-008).</summary>
    public int PayoutAttempt { get; private set; } = 1;

    /// <summary>DE-008: the last failed payout, its reference (which a retry or cancellation must be given, typed) and why it failed.</summary>
    public Guid? FailedPaymentId { get; private set; }

    public string? FailedPaymentRef { get; private set; }

    public string? FailureReason { get; private set; }

    /// <summary>DE-008: a PAYOUT_RETRY approval is waiting.</summary>
    public bool RetryPending { get; private set; }

    /// <summary>Who asked for the latest retry, to which of the payer's accounts and why; cleared if it is not granted.</summary>
    public Guid? RetryRequestedBy { get; private set; }

    public Guid? RetryBankAccountId { get; private set; }

    public string? RetryPayeeBankHash { get; private set; }

    public string? RetryReason { get; private set; }

    /// <summary>Who granted the latest retry, and its Workflow approval.</summary>
    public Guid? RetryDecidedBy { get; private set; }

    public Guid? RetryApprovalId { get; private set; }

    /// <summary>DE-008: why, and by whom, the refund was cancelled after its payout failed.</summary>
    public string? CancelReason { get; private set; }

    public Guid? CancelledBy { get; private set; }

    /// <summary>What a PAYOUT_RETRY approval covers: the attempt, the failed payment, payee, bank account, amount and currency.</summary>
    public string RetrySubjectHash =>
        ApprovalSubject.Hash("PAYOUT_RETRY", Id, PayoutAttempt + 1, FailedPaymentRef, PayerPartyId, RetryBankAccountId, RetryPayeeBankHash, Amount, Currency);

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

    /// <summary>True when this checker's approval was already applied (approved, and perhaps paid since): a redelivered grant changes nothing.</summary>
    public bool GrantedBy(Guid checkerId) =>
        Status is RefundStatus.Approved or RefundStatus.Paid or RefundStatus.PaymentFailed or RefundStatus.Cancelled && DecidedBy == checkerId;

    /// <summary>
    /// Workflow's decision (ApproveRefund or RejectRefund). The checker is never the requester. Every ending other than
    /// approval (rejected, expired, withdrawn) rejects the refund for good; an expired or withdrawn one has no checker
    /// (Guid.Empty), so no checker is compared or recorded. A refund already rejected takes a redelivered ending unchanged.
    /// </summary>
    public Result Decide(bool approved, Guid checkerId, string? subjectHash, DateTimeOffset now, Guid? approvalId = null)
    {
        if (!approved && Status == RefundStatus.Rejected)
        {
            return Result.Success();
        }

        if (subjectHash != SubjectHash)
        {
            return Error.Conflict(BillingErrorCodes.ApprovalSubjectChanged, "The approval was for another payee account or amount.");
        }

        var hasChecker = approved || checkerId != Guid.Empty;
        if (hasChecker && checkerId == RequestedBy)
        {
            return MakerChecker.EnsureDifferentPeople(RequestedBy.ToString()!, checkerId.ToString());
        }

        var result = Fire(approved ? RefundTrigger.ApproveRefund : RefundTrigger.RejectRefund);
        if (result.IsSuccess)
        {
            DecidedBy = hasChecker ? checkerId : null;
            if (approved)
            {
                ApprovalId = approvalId;
                Raise(new RefundApproved(Id, BillingAccountId, PayerPartyId, BankAccountId!.Value, Amount, Currency, now));
            }
        }

        return result;
    }

    /// <summary>The payout was created for this refund (GAP-036): its ID is known before it completes. No status change.</summary>
    public void LinkPayment(Guid paymentId)
    {
        if (PaymentId is null && Status == RefundStatus.Approved && paymentId != FailedPaymentId)
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

    /// <summary>
    /// DE-008, transition "Payment failed": the refund's payout failed. Its reference and reason are kept, for the retry or cancellation that
    /// must name it. A redelivery, or the failure of a payout the refund has moved on from, changes nothing (false).
    /// </summary>
    public bool MarkPaymentFailed(Guid paymentId, string paymentRef, string reason)
    {
        if (Status != RefundStatus.Approved || paymentId == FailedPaymentId || (PaymentId is { } linked && linked != paymentId))
        {
            return false;
        }

        if (Fire(RefundTrigger.PaymentFailed).IsFailure)
        {
            return false;
        }

        (PaymentId, FailedPaymentId, FailedPaymentRef) = (paymentId, paymentId, Guard.MaxLength(Guard.NotNullOrWhiteSpace(paymentRef), 25));
        FailureReason = reason.Length <= 200 ? reason : reason[..200];
        return true;
    }

    /// <summary>
    /// DE-008, command RetryRefundPayout: pay the refund again, to the same or another of the payer's payable accounts, once a second person
    /// approves (PAYOUT_RETRY). Only after a failed payout, with no retry already waiting, and with the failed payment's reference typed.
    /// </summary>
    public Result RequestPayoutRetry(string typedPaymentRef, string reason, Guid bankAccountId, string bankHash, Guid requestedBy, DateTimeOffset now)
    {
        if (Status != RefundStatus.PaymentFailed || RetryPending)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition,
                RetryPending ? "A retry of this refund's payout is already waiting for approval." : "Only a refund whose payout failed is paid again.");
        }

        if (TypedReference(typedPaymentRef) is { } mismatch)
        {
            return mismatch;
        }

        (RetryPending, RetryRequestedBy, RetryBankAccountId, RetryPayeeBankHash, RetryReason, RetryDecidedBy) =
            (true, requestedBy, Guard.NotEmpty(bankAccountId), bankHash, Guard.MaxLength(Guard.NotNullOrWhiteSpace(reason), 200), null);
        Raise(new RefundPayoutRetryRequested(Id, requestedBy, PayoutAttempt + 1, Amount, Currency, FailedPaymentRef!, RetrySubjectHash, now));
        MarkChanged();
        return Result.Success();
    }

    /// <summary>True when this PAYOUT_RETRY approval was already applied: a redelivered grant changes nothing.</summary>
    public bool RetryGrantedWith(Guid approvalId) => !RetryPending && RetryApprovalId == approvalId;

    /// <summary>
    /// DE-008: Workflow's decision on the retry. Granted, the refund is approved again on its next attempt, to the account asked for, and its
    /// new payout is requested; any other ending (rejected, expired, withdrawn) leaves it PaymentFailed with the request cleared. The checker is
    /// never the requester; an expired or withdrawn approval has no checker. A redelivered ending changes nothing.
    /// </summary>
    public Result DecideRetry(bool approved, Guid checkerId, string? subjectHash, Guid approvalId, DateTimeOffset now)
    {
        if (!RetryPending)
        {
            return approved && RetryApprovalId != approvalId
                ? Error.Conflict(ErrorCodes.InvalidStateTransition, "No retry of this refund's payout is waiting.")
                : Result.Success();
        }

        if (subjectHash != RetrySubjectHash)
        {
            return Error.Conflict(BillingErrorCodes.ApprovalSubjectChanged, "The approval was for another attempt, payee account or amount.");
        }

        var hasChecker = approved || checkerId != Guid.Empty;
        if (hasChecker && checkerId == RetryRequestedBy)
        {
            return MakerChecker.EnsureDifferentPeople(RetryRequestedBy.ToString()!, checkerId.ToString());
        }

        if (!approved)
        {
            (RetryPending, RetryRequestedBy, RetryBankAccountId, RetryPayeeBankHash, RetryReason) = (false, null, null, null, null);
            MarkChanged();
            return Result.Success();
        }

        var fired = Fire(RefundTrigger.RetryGranted);
        if (fired.IsFailure)
        {
            return fired;
        }

        PayoutAttempt++;
        (BankAccountId, PayeeBankHash, PaymentId) = (RetryBankAccountId, RetryPayeeBankHash, null);
        (RetryPending, RetryDecidedBy, RetryApprovalId) = (false, checkerId, approvalId);
        Raise(new RefundPayoutRetryGranted(Id, PayoutAttempt, Amount, Currency, PayerPartyId, BankAccountId!.Value, now));
        return Result.Success();
    }

    /// <summary>
    /// DE-008, command CancelRefund: after a failed payout, the refund is not paid. Nothing has left the account: a refund is posted only
    /// when its payout succeeds (REFUND_PAID), so the money simply stays with the account as credit. No approval, since no money moves.
    /// </summary>
    public Result Cancel(string typedPaymentRef, string reason, Guid cancelledBy, DateTimeOffset now)
    {
        if (Status != RefundStatus.PaymentFailed || RetryPending)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition,
                RetryPending ? "A retry of this refund's payout is waiting for approval; it is cancelled only once that has ended." : "Only a refund whose payout failed is cancelled.");
        }

        if (TypedReference(typedPaymentRef) is { } mismatch)
        {
            return mismatch;
        }

        var fired = Fire(RefundTrigger.CancelRefund);
        if (fired.IsFailure)
        {
            return fired;
        }

        (CancelReason, CancelledBy) = (Guard.MaxLength(Guard.NotNullOrWhiteSpace(reason), 200), cancelledBy);
        Raise(new RefundCancelled(Id, CancelReason, now));
        return Result.Success();
    }

    /// <summary>Ade's rule for money steps (24 November 2026): the failed payment's reference, typed by the person, must be the one recorded.</summary>
    private Error? TypedReference(string typed) =>
        string.Equals(typed?.Trim(), FailedPaymentRef, StringComparison.Ordinal)
            ? null
            : Error.Validation(ErrorCodes.TypedReferenceMismatch, "The payment reference typed isn't the failed payout's, so nothing was done.");

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
