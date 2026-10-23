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
    public const string PayoutCreate = "payout_batch.create";
    public const string PayoutRelease = "payout_batch.release";
    public const string ReconciliationRead = "reconciliation.read";
    public const string ReconciliationUpdate = "reconciliation.update";

    /// <summary>Held by a payment provider's service client, to report payment statuses.</summary>
    public const string CallbackCreate = "payment_callback.create";
}

public static class PaymentApprovals
{
    public const string PayoutBusinessRefType = "PAYOUT_BATCH";
    public const string PayoutApprovalType = "PAYOUT_RELEASE";
}

/// <summary>Who a payment is for: purposes and the business records they pay.</summary>
public static class PaymentPurposes
{
    public const string Premium = "PREMIUM";
    public const string Refund = "REFUND";
    public const string ClaimPayout = "CLAIM_PAYOUT";
    public const string Recovery = "RECOVERY";
    public const string Commission = "COMMISSION";
}

/// <summary>Money was received and confirmed (Succeeded inbound). Never raised for an unknown outcome (PAY-005).</summary>
[IntegrationEvent("payment.PaymentReceived")]
public sealed record PaymentReceived(Guid PaymentId, string PaymentRef, string Purpose, decimal Amount, string Currency, DateOnly ValueDate,
    Guid? CounterpartyPartyId, string? BusinessRefType, Guid? BusinessRefId) : IIntegrationEvent;

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
