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
    public const string MembershipMigrate = "health_membership.migrate";
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
    public const string ComplexCaseRead = "health_complex_case.read";
    public const string ComplexCaseUpdate = "health_complex_case.update";

    /// <summary>The case manager function: who may be assigned a complex case (F035).</summary>
    public const string ComplexCaseManage = "health_complex_case.manage";

    /// <summary>The clinical function: reads and writes any complex case's clinical detail (OQ-143).</summary>
    public const string ClinicalRead = "health_case.read_clinical";
    public const string ChronicRead = "health_chronic.read";
    public const string ChronicCreate = "health_chronic.create";

    /// <summary>Decides a chronic registration; the decider also needs the clinical function (OQ-150).</summary>
    public const string ChronicDecide = "health_chronic.decide";
    public const string ChronicUpdate = "health_chronic.update";
    public const string ProgrammeRead = "health_programme.read";
    public const string ProgrammeCreate = "health_programme.create";
    public const string ProgrammeUpdate = "health_programme.update";
    public const string ReferralRead = "health_referral.read";
    public const string ReferralCreate = "health_referral.create";
    public const string ReferralDecide = "health_referral.decide";

    /// <summary>Records or withdraws the member's consent on a referral.</summary>
    public const string ReferralConsent = "health_referral.consent";
    public const string ProviderRead = "health_provider.read";
    public const string ProviderUpdate = "health_provider.update";
    public const string RunUpdate = "provider_payment_run.update";
    public const string RecoveryRead = "health_recovery.read";
    public const string RecoveryManage = "health_recovery.manage";
    public const string CreditNoteRead = "health_credit_note.read";
    public const string CreditNoteCreate = "health_credit_note.create";
}

/// <summary>What Health asks Workflow to approve (ADR-009).</summary>
public static class HealthApprovals
{
    public const string RunBusinessRefType = "HEALTH_PAYMENT_RUN";
    public const string RunApprovalType = "PROVIDER_PAYMENT_RUN";
    public const string RecoveryBusinessRefType = "HEALTH_RECOVERY";
    public const string RecoveryWriteOffApprovalType = "PROVIDER_RECOVERY_WRITE_OFF";
    public const string RecoveryFindingApprovalType = "PROVIDER_RECOVERY_FINDING";
    public const string CreditNoteBusinessRefType = "HEALTH_CREDIT_NOTE";
    public const string CreditNoteApprovalType = "PROVIDER_CREDIT_NOTE";
}

/// <summary>Business references Health gives Payments and Billing.</summary>
public static class HealthReferences
{
    /// <summary>The source domain of a recovery's invoice (5a.3): its PolicyRef is the recovery. Not a contribution, so it posts no premium.</summary>
    public const string RecoverySourceDomain = "HEALTH_REC";

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
[IntegrationEvent("health.HealthMembershipMigrated")]
public sealed record HealthMembershipMigrated(Guid MembershipId, string MemberNumber, string PlanCode, string OptionCode, DateOnly StartDate, Guid PayerPartyId, Guid BillingAccountRef, string CollectionMethod, string Currency, decimal MonthlyContribution,
    decimal EmployerContribution, Guid? SponsorPartyId, string? ProductCode) : IIntegrationEvent;

[IntegrationEvent("health.HealthMemberEnrolled")]
public sealed record HealthMemberEnrolled(Guid MembershipId, string MemberNumber, Guid PrincipalPartyId, Guid? SchemeId, string PlanCode, string OptionCode,
    DateOnly StartDate, Guid PayerPartyId, Guid BillingAccountRef, string CollectionMethod, string Currency, decimal? MonthlyContribution,
    decimal? EmployerContribution = null, Guid? SponsorPartyId = null, string? OrgNodeCode = null, string? ProductCode = null) : IIntegrationEvent;

/// <summary>The membership's status or its members changed; a new monthly contribution applies from <paramref name="EffectiveDate"/> when given.</summary>
/// <summary>
/// The membership is now on a product (addenda/health-plan-products.md): Billing gives its schedules the same product, so
/// its invoices from then on are scoped by it (F103).
/// </summary>
[IntegrationEvent("health.MembershipProductAssigned")]
public sealed record MembershipProductAssigned(Guid MembershipId, string MemberNumber, string ProductCode) : IIntegrationEvent;

[IntegrationEvent("health.HealthMemberStatusChanged")]
public sealed record HealthMemberStatusChanged(Guid MembershipId, string MemberNumber, string Status, DateOnly EffectiveDate, decimal? MonthlyContribution,
    decimal? EmployerContribution = null) : IIntegrationEvent;

/// <summary>A membership suspended for unpaid contributions reached the end of its notice (F006): Workflow asks a person whether to terminate it.</summary>
[IntegrationEvent("health.MembershipTerminationDue")]
public sealed record MembershipTerminationDue(Guid MembershipId, string MemberNumber, DateOnly ArrearsSince) : IIntegrationEvent;

/// <summary>A membership's contributions in arrears were paid (F006): any termination task is closed.</summary>
[IntegrationEvent("health.MembershipArrearsCleared")]
public sealed record MembershipArrearsCleared(Guid MembershipId, string MemberNumber) : IIntegrationEvent;

/// <summary>The Workflow reference for a membership's tasks.</summary>
public static class HealthMembershipTasks
{
    public const string BusinessRefType = "HEALTH_MEMBERSHIP";
}

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

/// <summary>
/// A paid claim was reversed: its benefit consumption was given back, Finance reverses its claim-paid journal, and the amount
/// is to be recovered (collected in Wave 5a batch 5a.3, OQ-139).
/// </summary>
/// <summary>F037: a care plan's activity is past its date; Workflow opens a task for the case manager. References only (HLT-008).</summary>
[IntegrationEvent("health.CareActivityOverdue")]
public sealed record CareActivityOverdue(Guid CaseId, string CaseNumber, Guid ActivityId, Guid? CaseManagerId) : IIntegrationEvent;

/// <summary>F086: an enrolment's review is due; Workflow asks a person to do it. Names no programme or condition.</summary>
[IntegrationEvent("health.ProgrammeReviewDue")]
public sealed record ProgrammeReviewDue(Guid ReviewId, Guid EnrolmentId, string EnrolmentNumber) : IIntegrationEvent;

/// <summary>F028: a provider's month is past a measure's threshold; Workflow asks a person to look. Counts and amounts only.</summary>
[IntegrationEvent("health.ProviderMeasureBreached")]
public sealed record ProviderMeasureBreached(Guid ResultId, string ProviderId, string MonthKey, string MeasureCode, decimal Value, decimal Threshold) : IIntegrationEvent;

[IntegrationEvent("health.MedicalClaimReversed")]
public sealed record MedicalClaimReversed(Guid ClaimId, string ClaimNumber, string? ProviderId, decimal Amount, string Currency, string Reason) : IIntegrationEvent;

[IntegrationEvent("health.UtilisationCaseConcluded")]
public sealed record UtilisationCaseConcluded(Guid CaseId, Guid MembershipId, string RuleCode, string Outcome) : IIntegrationEvent;

[IntegrationEvent("health.FraudCaseConcluded")]
public sealed record FraudCaseConcluded(Guid CaseId, string SubjectType, string SubjectRef, string OutcomeCode) : IIntegrationEvent;

/// <param name="BankAccountId">The provider's verified bank account (party.bank_account) the run was approved with; never its details.</param>
public sealed record RemittanceReleased(Guid RemittanceId, string ProviderId, Guid ProviderPartyId, Guid BankAccountId, decimal Amount, decimal Gross = 0, decimal Offset = 0, decimal Repaid = 0);

/// <summary>
/// An approved run was released: Payments pays each remittance in a payout batch; the run's claims are paid; Documents
/// generates each remittance's advice from <paramref name="RemittanceTemplate"/> (health.cfg_remittance_advice_formats).
/// </summary>
[IntegrationEvent("health.ProviderPaymentRunReleased")]
public sealed record ProviderPaymentRunReleased(Guid RunId, string RunCode, decimal Total, string Currency, IReadOnlyList<RemittanceReleased> Remittances, DateOnly ReleasedOn,
    string? RemittanceTemplate = null) : IIntegrationEvent;

/// <summary>Public query interface: whether a provider is in the provider register in force and ACTIVE (for linking a provider portal user to it).</summary>
public interface IProviderDirectory
{
    Task<bool> IsActive(Guid tenantId, string providerId, DateOnly day, CancellationToken cancellationToken);

    /// <summary>The provider as a party (health.mst_provider_register.party_id) in force on the day, for its notices; null when it has none.</summary>
    Task<Guid?> PartyOf(Guid tenantId, string providerId, DateOnly day, CancellationToken cancellationToken);
}

/// <summary>A medical claim was rejected after validation; the member is told (communications §6.3). The reason is a code, never clinical.</summary>
/// <summary>An overpayment is owed (5a.3). A reversal's was posted as CLAIM_PAID_REVERSED; a credit note's or finding's posts on this.</summary>
[IntegrationEvent("health.ProviderRecoveryRaised")]
public sealed record ProviderRecoveryRaised(Guid RecoveryId, string RecoveryNumber, string DebtorKind, string? ProviderId, string Source, decimal Amount, string Currency, DateOnly RaisedOn) : IIntegrationEvent;

[IntegrationEvent("health.ProviderRecoveryInvoiceRequested")]
public sealed record ProviderRecoveryInvoiceRequested(Guid RecoveryId, string RecoveryNumber, Guid PayerPartyId, decimal Amount, string Currency, DateOnly DueOn, string? CollectionMethod) : IIntegrationEvent;

[IntegrationEvent("health.ProviderRecoveryOffsetTaken")]
public sealed record ProviderRecoveryOffsetTaken(Guid OffsetId, Guid RecoveryId, Guid RunId, Guid RemittanceId, decimal Amount, string Currency, DateOnly TakenOn) : IIntegrationEvent;

[IntegrationEvent("health.ProviderRecoveryRepaid")]
public sealed record ProviderRecoveryRepaid(Guid RecoveryId, Guid RunId, decimal Amount, string Currency, DateOnly RepaidOn) : IIntegrationEvent;

[IntegrationEvent("health.ProviderRecoveryWrittenOff")]
public sealed record ProviderRecoveryWrittenOff(Guid RecoveryId, string RecoveryNumber, string? ProviderId, decimal Amount, string Currency, DateOnly WrittenOffOn) : IIntegrationEvent;

[IntegrationEvent("health.ProviderRecoveryReversed")]
public sealed record ProviderRecoveryReversed(Guid RecoveryId, string RecoveryNumber, string? ProviderId, string Source, decimal Amount, string Currency, DateOnly ReversedOn) : IIntegrationEvent;

[IntegrationEvent("health.ProviderRecoverySettled")]
public sealed record ProviderRecoverySettled(Guid RecoveryId, decimal Amount, string Currency, DateOnly SettledOn) : IIntegrationEvent;

/// <summary>A person decides: a recovery uncollected for the tenant's period, or a member's overpayment (OQ-139, OQ-145).</summary>
[IntegrationEvent("health.ProviderRecoveryReviewDue")]
public sealed record ProviderRecoveryReviewDue(Guid RecoveryId, string RecoveryNumber, string DebtorKind, string? ProviderId) : IIntegrationEvent;

[IntegrationEvent("health.MedicalClaimRejected")]
public sealed record MedicalClaimRejected(Guid ClaimId, string ClaimNumber, Guid MembershipId, string ReasonCode) : IIntegrationEvent;

/// <summary>
/// Public query interface: who a Health notice goes to (communications §6.3): the patient, or the membership's principal
/// when the patient is a dependant under 18 (OQ-084). Null for an unknown claim or authorisation.
/// </summary>
public interface IHealthNoticeRecipients
{
    Task<NoticeRecipient?> ForClaim(Guid tenantId, Guid claimId, CancellationToken cancellationToken);

    Task<NoticeRecipient?> ForPreauthorisation(Guid tenantId, Guid preauthorisationId, CancellationToken cancellationToken);
}
