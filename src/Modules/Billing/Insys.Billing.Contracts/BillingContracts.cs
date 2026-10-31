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
using Insys.BuildingBlocks.Contracts;

namespace Insys.Billing.Contracts;

public static class BillingPermissions
{
    public const string AccountRead = "billing_account.read";
    public const string AccountCreate = "billing_account.create";
    public const string AccountUpdate = "billing_account.update";
    public const string InvoiceRead = "invoice.read";
    public const string InvoiceCreate = "invoice.create";
    public const string RefundRead = "refund.read";
    public const string RefundCreate = "refund.create";
}

public static class BillingApprovals
{
    public const string RefundBusinessRefType = "REFUND_REQUEST";
    public const string RefundApprovalType = "REFUND";
}

[IntegrationEvent("billing.BillingAccountOpened")]
public sealed record BillingAccountOpened(Guid AccountId, string AccountNumber, Guid PayerPartyId, string Currency) : IIntegrationEvent;

/// <summary>
/// An invoice was issued (BIL-002): the source policy, product version, currency and dates. <paramref name="PaymentMethod"/>
/// is the account's collection method's payment method: Payments collects it when a provider supports that method.
/// </summary>
[IntegrationEvent("billing.InvoiceIssued")]
public sealed record InvoiceIssued(Guid InvoiceId, string InvoiceNumber, Guid BillingAccountId, Guid PayerPartyId, string PaymentMethod, string SourceDomain, Guid PolicyRef,
    string ProductVersionRef, DateOnly IssueDate, DateOnly DueDate, string Currency, decimal NetAmount, decimal TaxAmount, decimal TotalAmount, string? TaxCode = null) : IIntegrationEvent;

/// <summary>A credit note corrects an issued invoice (BIL-004); the invoice itself never changes.</summary>
[IntegrationEvent("billing.CreditNoteIssued")]
public sealed record CreditNoteIssued(Guid CreditNoteId, string CreditNoteNumber, Guid InvoiceId, Guid BillingAccountId, string SourceDomain, Guid PolicyRef,
    DateOnly IssueDate, string Currency, decimal TotalAmount, string ReasonCode, decimal TaxAmount = 0, string? TaxCode = null) : IIntegrationEvent;

[IntegrationEvent("billing.InvoiceSettled")]
/// <summary>An invoice was paid in full; <paramref name="PolicyRef"/> is the policy it bills in <paramref name="SourceDomain"/>.</summary>
public sealed record InvoiceSettled(Guid InvoiceId, Guid BillingAccountId, string SourceDomain, Guid PolicyRef) : IIntegrationEvent;

/// <summary>
/// An issued invoice passed its due date unpaid (transition "Due date passed"). Billing's own consumer opens the account's
/// arrears case from it, and parks it while no ageing bucket covers the days overdue (ADR-022).
/// </summary>
[IntegrationEvent("billing.InvoiceOverdue")]
public sealed record InvoiceOverdue(Guid InvoiceId, Guid BillingAccountId, string SourceDomain, Guid PolicyRef, DateOnly DueDate, decimal Outstanding, string Currency)
    : IIntegrationEvent;

/// <summary>
/// An open arrears case aged past every ageing bucket in force (request 46). Billing's own consumer ages it on once a
/// bucket covers its days, and the delivery is parked until then (ADR-022), which raises the parked-delivery alert.
/// </summary>
[IntegrationEvent("billing.ArrearsAgeingBlocked")]
public sealed record ArrearsAgeingBlocked(Guid AccountId, string BucketCode, int DaysOverdue) : IIntegrationEvent;

/// <param name="PayerPartyId">The account's payer, who is reminded (communications §6.1); null on events raised before Wave 1.</param>
/// <param name="NextAction">The bucket's action: NONE, REMINDER, FINAL_NOTICE, LAPSE or CANCEL (billing.cfg_arrears_ageing_buckets).</param>
/// <param name="ProductCode">Not yet carried: an account has no single product (its schedules do).</param>
/// <param name="DetectedOn">
/// The business date Billing detected the arrears on (BR-103). A consumer counts overdue items as of this date, never the
/// date it processes the event, so a late or retried delivery counts the same. Null on events raised before BR-103.
/// </param>
/// <param name="OverdueInvoices">The account's invoices unpaid past their due date on <paramref name="DetectedOn"/>.</param>
[IntegrationEvent("billing.ArrearsDetected")]
public sealed record ArrearsDetected(Guid AccountId, string BucketCode, int DaysOverdue, decimal AmountOverdue, string Currency, string? AccountNumber = null,
    Guid? PayerPartyId = null, string? NextAction = null, string? ProductCode = null, string? OrgNodeCode = null, DateOnly? DetectedOn = null,
    int? OverdueInvoices = null) : IIntegrationEvent;

/// <summary>A refund was approved by a second person (SRS-002): Payments pays it.</summary>
[IntegrationEvent("billing.RefundApproved")]
/// <summary>A refund was approved, to the payer's bank account (party.bank_account) by id; never the account's details.</summary>
public sealed record RefundApproved(Guid RefundId, Guid BillingAccountId, Guid PayerPartyId, Guid BankAccountId, decimal Amount, string Currency) : IIntegrationEvent;

public sealed record PlannedAllocation(Guid InvoiceId, decimal Amount);

/// <summary>
/// Public query interface: how a received amount is spread over a billing account's open invoices, by the allocation
/// rules in force (BIL-003). Payments records the allocations; Billing applies them when PaymentAllocated arrives.
/// </summary>
public interface IAllocationPlanner
{
    /// <param name="billingAccountId">The payer's account; found from the invoice when not given.</param>
    /// <param name="invoiceId">The invoice the payment was for, allocated first; then the account's open invoices by the rule's priority.</param>
    Task<IReadOnlyList<PlannedAllocation>> Plan(Guid tenantId, Guid? billingAccountId, Guid? invoiceId, decimal amount, string currency, DateOnly day,
        CancellationToken cancellationToken);
}

/// <summary>A policy's instalments past their due date whose invoice is unpaid, and the oldest due date among them.</summary>
/// <param name="Outstanding">What remains unpaid on the overdue invoices (total less allocated and credited), in the policy's currency.</param>
public sealed record PolicyArrears(int OverdueInstalments, DateOnly? OldestDueDate, decimal Outstanding = 0m);

/// <summary>
/// Public query interface: a policy's arrears on its invoices, read on Billing's own connection, for the domain that
/// counts missed premiums (Life, process "Missed premium to lapse").
/// </summary>
public interface IPolicyArrears
{
    /// <summary>How many instalments a billing frequency has in a year (billing.ref_billing_frequencies.periods_per_year), if it is in force.</summary>
    Task<int?> PeriodsPerYear(Guid tenantId, string frequencyCode, DateOnly day, CancellationToken cancellationToken);

    Task<PolicyArrears> For(Guid tenantId, string sourceDomain, Guid policyRef, DateOnly asOf, CancellationToken cancellationToken);
}
