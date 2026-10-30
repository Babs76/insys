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
using Insys.BuildingBlocks.Contracts;

namespace Insys.Payments.Contracts;

public static class PaymentPermissions
{
    public const string PaymentRead = "payment.read";
    public const string PaymentCreate = "payment.create";
    public const string PaymentUpdate = "payment.update";
    public const string PaymentResolve = "payment.resolve";
    public const string PayoutRead = "payout_batch.read";

    /// <summary>Reading commission payouts and their batches: outside branch scope, but held only by finance roles that need it.</summary>
    public const string CommissionPayoutRead = "commission_payout.read";
    public const string PayoutCreate = "payout_batch.create";
    public const string PayoutRelease = "payout_batch.release";
    public const string ReconciliationRead = "reconciliation.read";
    public const string ReconciliationUpdate = "reconciliation.update";

    /// <summary>Held by a payment provider's service client, to report payment statuses.</summary>
    public const string CallbackCreate = "payment_callback.create";
    public const string CallbackEndpointRead = "payment_callback_endpoint.read";
    public const string CallbackEndpointUpdate = "payment_callback_endpoint.update";
}

/// <summary>The function a provider's client-credentials service identity holds (ADR-023 decision 4).</summary>
public static class PaymentFunctionCodes
{
    public const string CallbackClient = "PAYMENTS_CALLBACK_CLIENT";
}

public static class PaymentApprovals
{
    public const string PayoutBusinessRefType = "PAYOUT_BATCH";
    public const string PayoutApprovalType = "PAYOUT_RELEASE";

    /// <summary>BR-042: a payment; its approvals open the payments screen.</summary>
    public const string PaymentBusinessRefType = "PAYMENT";

    /// <summary>BR-042: resolving an Unknown payment as succeeded without a matched bank statement line needs a second person.</summary>
    public const string UnknownResolutionApprovalType = "UNKNOWN_PAYMENT_RESOLUTION";
}

/// <summary>Who a payment is for: purposes and the business records they pay.</summary>
public static class PaymentPurposes
{
    public const string Premium = "PREMIUM";
    public const string Refund = "REFUND";
    public const string ClaimPayout = "CLAIM_PAYOUT";
    public const string Recovery = "RECOVERY";
    public const string Commission = "COMMISSION";

    /// <summary>Compensation or goodwill on a case (OQ-104): Workflow's CaseApprovals.RedressPaymentPurpose.</summary>
    public const string CaseRedress = "CASE_REDRESS";
}

/// <summary>Money was received and confirmed (Succeeded inbound). Never raised for an unknown outcome (PAY-005).</summary>
[IntegrationEvent("payment.PaymentReceived")]
public sealed record PaymentReceived(Guid PaymentId, string PaymentRef, string Purpose, decimal Amount, string Currency, DateOnly ValueDate,
    Guid? CounterpartyPartyId, string? BusinessRefType, Guid? BusinessRefId) : IIntegrationEvent;

/// <summary>
/// Business reference types a payment carries (GAP-037): an open set, since each module names its own, listed as known
/// values on the payments lookup.
/// </summary>
public static class PaymentReferenceTypes
{
    public const string Invoice = "INVOICE";

    /// <summary>Redress approved on a case (OQ-104): Workflow's CaseApprovals.RedressBusinessRefType, with the redress ID.</summary>
    public const string CaseRedress = "CASE_REDRESS";

    public static readonly IReadOnlyList<string> Known =
        [Invoice, "REFUND_REQUEST", "CLAIM_PAYMENT", "HEALTH_REMITTANCE", "COMMISSION_STATEMENT", CaseRedress];
}

/// <summary>
/// A payment was created (GAP-036), before it is sent or completes: Billing and the Claims Framework record its ID on
/// the refund or payment instruction it serves.
/// </summary>
[IntegrationEvent("payment.PaymentInitiated")]
public sealed record PaymentInitiated(Guid PaymentId, string PaymentRef, string Direction, string Purpose, decimal Amount, string Currency, string? BusinessRefType,
    Guid? BusinessRefId) : IIntegrationEvent;

/// <summary>An outbound payment was confirmed by the provider. Never raised for an unknown outcome (PAY-005).</summary>
[IntegrationEvent("payment.PaymentSucceeded")]
public sealed record PaymentSucceeded(Guid PaymentId, string PaymentRef, string Purpose, decimal Amount, string Currency, DateOnly ValueDate,
    string? BusinessRefType, Guid? BusinessRefId) : IIntegrationEvent;

[IntegrationEvent("payment.PaymentFailed")]
public sealed record PaymentFailed(Guid PaymentId, string PaymentRef, string Direction, string Purpose, decimal Amount, string Currency, string? BusinessRefType,
    Guid? BusinessRefId, string Reason) : IIntegrationEvent;

/// <summary>A payment's outcome is not known: it goes to reconciliation and is never treated as succeeded (PAY-005).</summary>
[IntegrationEvent("payment.PaymentOutcomeUnknown")]
public sealed record PaymentOutcomeUnknown(Guid PaymentId, string PaymentRef, string Direction, string Purpose, decimal Amount, string Currency) : IIntegrationEvent;

[IntegrationEvent("payment.PaymentAllocated")]
public sealed record PaymentAllocated(Guid PaymentId, Guid AllocationId, Guid InvoiceId, decimal Amount, string Currency) : IIntegrationEvent;

public sealed record ReversedAllocation(Guid AllocationId, Guid InvoiceId, decimal Amount);

/// <summary>A succeeded payment was reversed: its allocations are reversed too, so the invoices reopen.</summary>
[IntegrationEvent("payment.PaymentReversed")]
public sealed record PaymentReversed(Guid PaymentId, string PaymentRef, string Direction, string Purpose, decimal Amount, string Currency, DateOnly ReversedOn,
    IReadOnlyList<ReversedAllocation> ReversedAllocations, string Reason) : IIntegrationEvent;

[IntegrationEvent("payment.PayoutReleased")]
public sealed record PayoutReleased(Guid BatchId, string BatchRef, string Purpose, int PaymentCount, decimal TotalAmount, string Currency) : IIntegrationEvent;

[IntegrationEvent("payment.ReconciliationExceptionRaised")]
public sealed record ReconciliationExceptionRaised(Guid ExceptionId, string ExceptionType, Guid? PaymentId, Guid? StatementLineId, decimal Amount) : IIntegrationEvent;
