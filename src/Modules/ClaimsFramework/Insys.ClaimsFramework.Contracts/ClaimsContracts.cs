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

/// <summary>
/// The product and organisation node of the domain claim, which scope its Claims Framework records (payment instructions,
/// reserves, evidence) as the domain claim is scoped (F103, addenda/access-scopes.md §1).
/// </summary>
public sealed record ClaimOwner(string ProductCode, string? OrgNodeCode);

/// <summary>The outcome of registering a domain claim: its claim number, or why it could not be allocated.</summary>
public sealed record ClaimRegistrationResult(bool Succeeded, string? ClaimNumber, string? ErrorCode, string? ErrorMessage);

/// <summary>
/// Open Host Service (domain-model.json claims → domains: claim numbers). A domain registering a claim gets its claim
/// number here: idempotent per domain and domain claim (CLM-005). The fraud indicators in force are evaluated against
/// the facts given; an indicator that cannot be evaluated does not stop the registration.
/// </summary>
public interface IClaimRegistry
{
    /// <param name="owner">The domain claim's product and node (F103).</param>
    /// <param name="initialReserve">The reserve the claim starts with, if any (OQ-079).</param>
    Task<ClaimRegistrationResult> Register(string domain, Guid domainClaimId, Guid policyRef, ClaimOwner owner, System.Text.Json.JsonElement fraudFacts,
        InitialReserve? initialReserve, CancellationToken cancellationToken);
}

/// <summary>The decision types of claims.ref_claim_decision_reason_codes: a reason is given for one kind of decision.</summary>
public static class ClaimDecisionTypes
{
    public const string Approve = "APPROVE";

    /// <summary>A partial approval: settled below the claimed amount (OQ-102, F010).</summary>
    public const string Partial = "PARTIAL";

    public const string Decline = "DECLINE";
    public const string Withdraw = "WITHDRAW";
}

/// <summary>The outcome a claim module records with its decision (OQ-102): a partial approval is its own outcome.</summary>
public static class ClaimOutcomes
{
    public const string Approved = "APPROVED";
    public const string PartlyApproved = "PARTLY_APPROVED";
    public const string Declined = "DECLINED";
}

/// <summary>A claim decision reason in force: its decision type and the wording a claimant is given.</summary>
public sealed record ClaimDecisionReason(string Code, string DecisionType, string CustomerWording);

/// <summary>
/// Public query interface: the tenant's claim decision reasons (claims.ref_claim_decision_reason_codes), for a claim
/// module's decision and the reasons its notices give (OQ-102). Read on the Claims Framework's own connection.
/// </summary>
public interface IClaimDecisionReasons
{
    /// <summary>The reason in force on the day, or null.</summary>
    Task<ClaimDecisionReason?> InForce(Guid tenantId, string code, DateOnly day, CancellationToken cancellationToken);
}
