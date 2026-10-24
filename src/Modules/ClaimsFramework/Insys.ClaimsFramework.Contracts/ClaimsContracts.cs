/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : ClaimsFramework
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

namespace Insys.ClaimsFramework.Contracts;

public static class ClaimsPermissions
{
    public const string ClaimRead = "claim_register.read";
    public const string ClaimCreate = "claim_register.create";
    public const string ClaimUpdate = "claim_register.update";
    public const string FinancialsRead = "claim_financials.read";
    public const string FinancialsUpdate = "claim_financials.update";
    public const string InstructionRead = "claim_payment.read";
    public const string InstructionCreate = "claim_payment.create";
    public const string InstructionRelease = "claim_payment.release";
}

public static class ClaimsApprovals
{
    public const string InstructionBusinessRefType = "CLAIM_PAYMENT";
    public const string InstructionApprovalType = "CLAIM_PAYMENT";
}

[IntegrationEvent("claims.ClaimNumberAllocated")]
public sealed record ClaimNumberAllocated(string ClaimNumber, string Domain, Guid DomainClaimId, Guid PolicyRef) : IIntegrationEvent;

/// <summary>An indicator skipped on a claim, and why (F009): MISSING_FACTS with the facts' names, or EVALUATION_FAILED with the error code.</summary>
public sealed record SkippedFraudIndicator(string IndicatorCode, string Reason, IReadOnlyList<string> MissingFacts, string? ErrorCode);

/// <summary>
/// Fraud indicators could not be evaluated on a claim (F009). Workflow opens a configuration review task for the
/// tenant's configuration owner role, one per indicator while it stays open.
/// </summary>
[IntegrationEvent("claims.FraudIndicatorsNotEvaluated")]
public sealed record FraudIndicatorsNotEvaluated(Guid EvaluationId, string ClaimNumber, IReadOnlyList<SkippedFraudIndicator> Skipped) : IIntegrationEvent;

[IntegrationEvent("claims.FraudIndicatorRaised")]
public sealed record FraudIndicatorRaised(string ClaimNumber, string IndicatorCode, int Score, bool Referred) : IIntegrationEvent;

/// <summary>A reserve was set or changed (CLM-004). <paramref name="Change"/> is signed: positive raises the reserve.</summary>
[IntegrationEvent("claims.ClaimReserveChanged")]
public sealed record ClaimReserveChanged(Guid MovementId, string ClaimNumber, string Domain, string ReserveType, decimal Change, decimal Outstanding, string Currency,
    DateOnly MovementDate) : IIntegrationEvent;

[IntegrationEvent("claims.ClaimRecoveryRecorded")]
public sealed record ClaimRecoveryRecorded(Guid MovementId, string ClaimNumber, string Domain, string ReserveType, decimal Amount, string Currency,
    DateOnly MovementDate) : IIntegrationEvent;

/// <summary>An approved instruction was released: Payments adds it to a payout batch. A retry is a new attempt, and a new payment (PAY-001).</summary>
[IntegrationEvent("claims.ClaimPaymentInstructed")]
/// <param name="BankAccountId">The payee's bank account (party.bank_account) the payment was approved to; never the account's details.</param>
public sealed record ClaimPaymentInstructed(Guid InstructionId, string ClaimNumber, string Domain, Guid PayeePartyId, Guid BankAccountId, decimal Amount, string Currency,
    int Attempt) : IIntegrationEvent;

/// <summary>
/// An approved claim payment instruction was not released automatically, and is held (RESERVE_BELOW_ZERO,
/// PAYEE_ACCOUNT_CHANGED). Workflow opens a task for <paramref name="TargetRole"/>.
/// </summary>
[IntegrationEvent("claims.ClaimPaymentReleaseHeld")]
public sealed record ClaimPaymentReleaseHeld(Guid InstructionId, string ClaimNumber, string Domain, string Reason, decimal Amount, string Currency, string ReserveType,
    string TargetRole) : IIntegrationEvent;

[IntegrationEvent("claims.ClaimPaymentCompleted")]
public sealed record ClaimPaymentCompleted(Guid InstructionId, Guid MovementId, string ClaimNumber, string Domain, string ReserveType, decimal Amount, string Currency,
    DateOnly PaidOn) : IIntegrationEvent;

/// <summary>
/// The reserve a claim starts with, set at registration (OQ-079: a Life lump-sum claim's reserve is the sum assured of each
/// claimed cover). The assessor adjusts it afterwards with a reason.
/// </summary>
public sealed record InitialReserve(string ReserveType, decimal Amount, string Currency, string Basis);

/// <summary>The outcome of registering a domain claim: its claim number, or why it could not be allocated.</summary>
public sealed record ClaimRegistrationResult(bool Succeeded, string? ClaimNumber, string? ErrorCode, string? ErrorMessage);

/// <summary>
/// Open Host Service (domain-model.json claims → domains: claim numbers). A domain registering a claim gets its claim
/// number here: idempotent per domain and domain claim (CLM-005). The fraud indicators in force are evaluated against
/// the facts given; an indicator that cannot be evaluated does not stop the registration.
/// </summary>
public interface IClaimRegistry
{
    Task<ClaimRegistrationResult> Register(string domain, Guid domainClaimId, Guid policyRef, System.Text.Json.JsonElement fraudFacts, CancellationToken cancellationToken);

    /// <summary>As <see cref="Register(string, Guid, Guid, System.Text.Json.JsonElement, CancellationToken)"/>, with the reserve the claim starts with (OQ-079).</summary>
    Task<ClaimRegistrationResult> Register(string domain, Guid domainClaimId, Guid policyRef, System.Text.Json.JsonElement fraudFacts, InitialReserve? initialReserve,
        CancellationToken cancellationToken);
}
