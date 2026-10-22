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
    string ProductVersionRef, DateOnly IssueDate, DateOnly DueDate, string Currency, decimal NetAmount, decimal TaxAmount, decimal TotalAmount) : IIntegrationEvent;

/// <summary>A credit note corrects an issued invoice (BIL-004); the invoice itself never changes.</summary>
[IntegrationEvent("billing.CreditNoteIssued")]
public sealed record CreditNoteIssued(Guid CreditNoteId, string CreditNoteNumber, Guid InvoiceId, Guid BillingAccountId, string SourceDomain, Guid PolicyRef,
    DateOnly IssueDate, string Currency, decimal TotalAmount, string ReasonCode) : IIntegrationEvent;

[IntegrationEvent("billing.InvoiceSettled")]
/// <summary>An invoice was paid in full; <paramref name="PolicyRef"/> is the policy it bills in <paramref name="SourceDomain"/>.</summary>
public sealed record InvoiceSettled(Guid InvoiceId, Guid BillingAccountId, string SourceDomain, Guid PolicyRef) : IIntegrationEvent;

[IntegrationEvent("billing.ArrearsDetected")]
public sealed record ArrearsDetected(Guid AccountId, string BucketCode, int DaysOverdue, decimal AmountOverdue, string Currency) : IIntegrationEvent;

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
public sealed record PolicyArrears(int OverdueInstalments, DateOnly? OldestDueDate);

/// <summary>
/// Public query interface: a policy's arrears on its invoices, read on Billing's own connection, for the domain that
/// counts missed premiums (Life, process "Missed premium to lapse").
/// </summary>
public interface IPolicyArrears
{
    Task<PolicyArrears> For(Guid tenantId, string sourceDomain, Guid policyRef, DateOnly asOf, CancellationToken cancellationToken);
}
