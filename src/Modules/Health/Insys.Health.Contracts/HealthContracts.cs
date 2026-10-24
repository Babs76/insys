/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Health
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

namespace Insys.Health.Contracts;

public static class HealthPermissions
{
    public const string SchemeRead = "health_scheme.read";
    public const string SchemeCreate = "health_scheme.create";
    public const string SchemeUpdate = "health_scheme.update";
    public const string MembershipRead = "health_membership.read";
    public const string MembershipCreate = "health_membership.create";
    public const string MembershipUpdate = "health_membership.update";

    /// <summary>Authorised eligibility enquiries (HLT-003), for providers and staff.</summary>
    public const string EligibilityCheck = "health_eligibility.create";

    public const string PreauthRead = "health_preauthorisation.read";
    public const string PreauthCreate = "health_preauthorisation.create";

    /// <summary>Clinical review and decisions need a clinical purpose (HLT-008).</summary>
    public const string PreauthReview = "health_preauthorisation.review";

    public const string ClaimRead = "medical_claim.read";
    public const string ClaimCreate = "medical_claim.create";
    public const string ClaimUpdate = "medical_claim.update";
    public const string ClaimApprove = "medical_claim.approve";
    public const string AccumulatorRead = "benefit_accumulator.read";
    public const string CaseRead = "health_case.read";
    public const string CaseUpdate = "health_case.update";
    public const string RunRead = "provider_payment_run.read";
    public const string RunUpdate = "provider_payment_run.update";
}

/// <summary>What Health asks Workflow to approve (ADR-009).</summary>
public static class HealthApprovals
{
    public const string RunBusinessRefType = "HEALTH_PAYMENT_RUN";
    public const string RunApprovalType = "PROVIDER_PAYMENT_RUN";
}

/// <summary>Business references Health gives Payments and Billing.</summary>
public static class HealthReferences
{
    public const string SourceDomain = "HEALTH";

    /// <summary>The employer's share of a scheme member's contribution is billed to the sponsor under this source domain.</summary>
    public const string EmployerSourceDomain = "HEALTH_ER";
    public const string RemittanceRefType = "HEALTH_REMITTANCE";
}

[IntegrationEvent("health.HealthSchemeRenewed")]
public sealed record HealthSchemeRenewed(Guid SchemeId, string SchemeNumber, DateOnly RenewalDate) : IIntegrationEvent;

/// <summary>
/// A member was enrolled: Billing opens the payer's account (if new) and a monthly contribution schedule from the
/// start date when the contribution is known (scheme members; health.cfg_contribution_rules). The employer's share is
/// billed to the sponsor's party on a schedule of its own.
/// </summary>
[IntegrationEvent("health.HealthMemberEnrolled")]
public sealed record HealthMemberEnrolled(Guid MembershipId, string MemberNumber, Guid PrincipalPartyId, Guid? SchemeId, string PlanCode, string OptionCode,
    DateOnly StartDate, Guid PayerPartyId, Guid BillingAccountRef, string CollectionMethod, string Currency, decimal? MonthlyContribution,
    decimal? EmployerContribution = null, Guid? SponsorPartyId = null, string? OrgNodeCode = null, string? ProductCode = null) : IIntegrationEvent;

/// <summary>The membership's status or its members changed; a new monthly contribution applies from <paramref name="EffectiveDate"/> when given.</summary>
[IntegrationEvent("health.HealthMemberStatusChanged")]
public sealed record HealthMemberStatusChanged(Guid MembershipId, string MemberNumber, string Status, DateOnly EffectiveDate, decimal? MonthlyContribution,
    decimal? EmployerContribution = null) : IIntegrationEvent;

[IntegrationEvent("health.DependantAdded")]
public sealed record DependantAdded(Guid MembershipId, string MemberNumber, Guid DependantId, Guid PartyId, string Suffix, DateOnly StartDate, decimal? MonthlyContribution,
    decimal? EmployerContribution = null) : IIntegrationEvent;

/// <summary>An eligibility enquiry's answer. Identifiers and codes only; no medical data (HLT-008).</summary>
[IntegrationEvent("health.EligibilityDecisionRecorded")]
public sealed record EligibilityDecisionRecorded(Guid DecisionId, string MemberNumber, string DependantSuffix, string? ProviderId, string ServiceType, string ResponseCode)
    : IIntegrationEvent;

/// <param name="LineId">The authorised line.</param>
/// <param name="Amount">Reserved when positive, released when negative.</param>
public sealed record ReservationChange(Guid LineId, string Category, decimal Amount);

/// <summary>An authorisation was approved: its lines' amounts are reserved on the benefit (process "Health care to provider payment").</summary>
[IntegrationEvent("health.PreauthorisationApproved")]
public sealed record PreauthorisationApproved(Guid PreauthorisationId, string AuthNumber, Guid MembershipId, Guid PatientPartyId, DateOnly ValidFrom, DateOnly ValidTo,
    IReadOnlyList<ReservationChange> Reservations) : IIntegrationEvent;

[IntegrationEvent("health.PreauthorisationRejected")]
public sealed record PreauthorisationRejected(Guid PreauthorisationId, string AuthNumber, string Reason) : IIntegrationEvent;

[IntegrationEvent("health.PreauthorisationExpired")]
public sealed record PreauthorisationExpired(Guid PreauthorisationId, string AuthNumber, DateOnly ValidTo) : IIntegrationEvent;

/// <summary>An authorisation's reservations changed (modified, cancelled, expired or used): Health reserves or releases them on the accumulators.</summary>
[IntegrationEvent("health.PreauthorisationReservationsChanged")]
public sealed record PreauthorisationReservationsChanged(Guid PreauthorisationId, string AuthNumber, Guid MembershipId, Guid PatientPartyId, DateOnly ValidFrom,
    IReadOnlyList<ReservationChange> Changes, string Why) : IIntegrationEvent;

[IntegrationEvent("health.BenefitLimitReached")]
public sealed record BenefitLimitReached(Guid AccumulatorId, Guid MembershipId, string BenefitCategory, DateOnly PeriodEnd) : IIntegrationEvent;

[IntegrationEvent("health.MedicalClaimSubmitted")]
public sealed record MedicalClaimSubmitted(Guid ClaimId, string ClaimNumber, string Source, string? ProviderId, Guid MembershipId, DateOnly ServiceDate,
    decimal TotalClaimed, string Currency) : IIntegrationEvent;

public sealed record AuthorisationUse(Guid PreauthorisationId, Guid LineId, decimal Released, decimal Consumed);

public sealed record HighCostLine(int LineNo, string RuleCode);

/// <summary>A claim was adjudicated: the authorisations it used are marked utilised; lines above a high-cost threshold open a utilisation review.</summary>
[IntegrationEvent("health.MedicalClaimAdjudicated")]
public sealed record MedicalClaimAdjudicated(Guid ClaimId, string ClaimNumber, Guid MembershipId, bool Pended, decimal TotalPaid, string Currency,
    IReadOnlyList<AuthorisationUse> AuthorisationUses, IReadOnlyList<HighCostLine> HighCost) : IIntegrationEvent;

/// <summary>A claim was paid by a provider payment run: Finance posts the claims paid (HLT-009).</summary>
[IntegrationEvent("health.MedicalClaimSettled")]
public sealed record MedicalClaimSettled(Guid ClaimId, string ClaimNumber, string? ProviderId, Guid PaymentRunId, decimal Amount, string Currency, DateOnly PaidOn)
    : IIntegrationEvent;

/// <summary>A paid claim was reversed: its benefit consumption was given back, and the amount is to be recovered. TODO(spec): recovery collection (OQ-061).</summary>
[IntegrationEvent("health.MedicalClaimReversed")]
public sealed record MedicalClaimReversed(Guid ClaimId, string ClaimNumber, string? ProviderId, decimal Amount, string Currency, string Reason) : IIntegrationEvent;

[IntegrationEvent("health.UtilisationCaseConcluded")]
public sealed record UtilisationCaseConcluded(Guid CaseId, Guid MembershipId, string RuleCode, string Outcome) : IIntegrationEvent;

[IntegrationEvent("health.FraudCaseConcluded")]
public sealed record FraudCaseConcluded(Guid CaseId, string SubjectType, string SubjectRef, string OutcomeCode) : IIntegrationEvent;

/// <param name="BankAccountId">The provider's verified bank account (party.bank_account) the run was approved with; never its details.</param>
public sealed record RemittanceReleased(Guid RemittanceId, string ProviderId, Guid ProviderPartyId, Guid BankAccountId, decimal Amount);

/// <summary>
/// An approved run was released: Payments pays each remittance in a payout batch; the run's claims are paid; Documents
/// generates each remittance's advice from <paramref name="RemittanceTemplate"/> (health.cfg_remittance_advice_formats).
/// </summary>
[IntegrationEvent("health.ProviderPaymentRunReleased")]
public sealed record ProviderPaymentRunReleased(Guid RunId, string RunCode, decimal Total, string Currency, IReadOnlyList<RemittanceReleased> Remittances, DateOnly ReleasedOn,
    string? RemittanceTemplate = null) : IIntegrationEvent;
