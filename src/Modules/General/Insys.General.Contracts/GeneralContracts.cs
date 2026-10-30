/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : General
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

namespace Insys.General.Contracts;

public static class GeneralPermissions
{
    public const string RiskRead = "general_risk_object.read";
    public const string RiskCreate = "general_risk_object.create";
    public const string RiskUpdate = "general_risk_object.update";
    public const string QuoteRead = "general_quote.read";
    public const string QuoteCreate = "general_quote.create";
    public const string QuoteUpdate = "general_quote.update";
    public const string QuoteUnderwrite = "general_quote.underwrite";
    public const string PolicyRead = "general_policy.read";
    public const string PolicyCreate = "general_policy.create";
    public const string PolicyUpdate = "general_policy.update";
    public const string PolicyCancel = "general_policy.cancel";

    /// <summary>Wave 4 §4.3: proposing an insurer's non-renewal (F025), and deciding one that needs approval.</summary>
    public const string PolicyNonRenew = "general_policy.non_renew";

    /// <summary>Wave 4 §4.4: recording the customer's acceptance or decline of a renewal.</summary>
    public const string RenewalRespond = "general_renewal.respond";

    /// <summary>F026: reading retention cases, and working them (open, offer, record answers, close).</summary>
    public const string RetentionRead = "general_retention.read";
    public const string RetentionUpdate = "general_retention.update";
    public const string EndorsementRead = "general_endorsement.read";
    public const string EndorsementCreate = "general_endorsement.create";
    public const string ClaimRead = "general_claim.read";
    public const string ClaimCreate = "general_claim.create";
    public const string ClaimUpdate = "general_claim.update";
    public const string ClaimDecide = "general_claim.decide";
    public const string AssessmentUpdate = "general_assessment.update";
    public const string RecoveryUpdate = "general_recovery.update";
    public const string AggregationAlertRead = "general_aggregation_alert.read";
}

/// <summary>What General asks Workflow to approve (ADR-009).</summary>
public static class GeneralApprovals
{
    public const string EndorsementBusinessRefType = "GENERAL_ENDORSEMENT";
    public const string EndorsementApprovalType = "ENDORSEMENT";
    public const string SettlementBusinessRefType = "GENERAL_CLAIM";
    public const string SettlementApprovalType = "CLAIM_SETTLEMENT";

    /// <summary>F025: an insurer's non-renewal whose reason needs a second person (Wave 4 §4.3).</summary>
    public const string NonRenewalBusinessRefType = "GENERAL_RENEWAL";
    public const string NonRenewalApprovalType = "GENERAL_NON_RENEWAL";
}

/// <summary>The subjects General rates through Rating (PremiumCalculated.SubjectType; PRD-008).</summary>
public static class GeneralRatingSubjects
{
    public const string SourceDomain = "GENERAL";
    public const string Quote = "GENERAL_QUOTE";
    public const string Endorsement = "GENERAL_ENDORSEMENT";
}

/// <summary>Document business references for General (DocumentGenerated.BusinessRefType).</summary>
public static class GeneralDocuments
{
    public const string PolicyRefType = "GENERAL_POLICY";
    public const string EndorsementRefType = "GENERAL_ENDORSEMENT";
}

[IntegrationEvent("general.GeneralRiskObjectRegistered")]
public sealed record GeneralRiskObjectRegistered(Guid RiskObjectId, string ObjectType) : IIntegrationEvent;

/// <summary>
/// A zone and peril's exposure in force reached the alert share of its limit (F062 §1), for operations and reinsurance.
/// Raised at most once a day per zone and peril, from the exposure snapshot, and recorded as an aggregation alert (GAP-051).
/// </summary>
/// <param name="AlertId">The recorded alert (GET /general/aggregation-alerts/{id}); null on events raised before GAP-051.</param>
/// <param name="AlertAtPct">The alert level that applied, in per cent of the limit.</param>
/// <param name="TargetRole">The role whose queue reviews the zone and peril (General:Aggregation:AlertRole, OQ-085).</param>
[IntegrationEvent("general.AggregationLimitApproached")]
public sealed record AggregationLimitApproached(string Zone, string Peril, decimal Exposure, decimal MaxExposure, string Currency, DateOnly AsAt,
    Guid? AlertId = null, decimal? AlertAtPct = null, string? TargetRole = null) : IIntegrationEvent;

/// <summary>A zone and peril as a business reference, for the Workflow review of its alerts (GAP-051).</summary>
public static class AggregationZones
{
    public const string BusinessRefType = "AGGREGATION_ZONE";

    /// <summary>The same reference for a zone and peril every time, so its open review task can be found again.</summary>
    public static Guid Reference(string zone, string peril)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{BusinessRefType}:{zone}|{peril}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}

/// <summary>
/// A quote priced as it is made (a broker's portal quote and its later versions, BR-095) asks Rating for its premium.
/// Rating answers with PremiumCalculated for subject GENERAL_QUOTE, or PremiumNotCalculated with the reason.
/// </summary>
/// <param name="Values">The rating inputs, as Rating's calculation request takes them: the risk objects' attributes and <c>sum_insured</c>.</param>
/// <param name="Frequency">The premium's frequency: a General quote's premium is annual.</param>
[IntegrationEvent("general.GeneralQuotePricingRequested")]
public sealed record GeneralQuotePricingRequested(Guid QuoteId, string QuoteNumber, int QuoteVersion, string ProductCode, int ProductVersion, string Currency,
    string Frequency, DateOnly RatingDate, System.Text.Json.JsonElement Values) : IIntegrationEvent;

/// <summary>A quote is referred to an underwriter: Workflow opens a referral task for a role whose underwriting authority covers it.</summary>
/// <param name="SumInsured">The quote's total sum insured, which the deciding authority must cover.</param>
[IntegrationEvent("general.GeneralQuoteReferred")]
public sealed record GeneralQuoteReferred(Guid QuoteId, string QuoteNumber, int QuoteVersion, string ProductCode, string ReasonCode, decimal SumInsured, string Currency)
    : IIntegrationEvent;

/// <summary>A quote's referral was decided: Workflow closes its referral task.</summary>
[IntegrationEvent("general.GeneralQuoteReferralDecided")]
public sealed record GeneralQuoteReferralDecided(Guid QuoteId, string QuoteNumber, int QuoteVersion, bool Approved, string DecisionCode) : IIntegrationEvent;

/// <summary>What a General quote's referral task is about (Workflow's business reference).</summary>
public static class GeneralReferrals
{
    public const string BusinessRefType = "GENERAL_QUOTE";
}

[IntegrationEvent("general.GeneralQuoteIssued")]
public sealed record GeneralQuoteIssued(Guid QuoteId, string QuoteNumber, int QuoteVersion, string ProductCode, int ProductVersion, decimal Premium, string Currency,
    DateOnly ValidUntil) : IIntegrationEvent;

[IntegrationEvent("general.GeneralQuoteAccepted")]
public sealed record GeneralQuoteAccepted(Guid QuoteId, string QuoteNumber, int QuoteVersion, Guid ProposerPartyId) : IIntegrationEvent;

/// <summary>An underwriter declined a referred quote; it is final. The reason stays with the referral.</summary>
[IntegrationEvent("general.GeneralQuoteDeclined")]
public sealed record GeneralQuoteDeclined(Guid QuoteId, string QuoteNumber, int QuoteVersion, string DecisionCode) : IIntegrationEvent;

[IntegrationEvent("general.GeneralQuoteExpired")]
public sealed record GeneralQuoteExpired(Guid QuoteId, string QuoteNumber, int QuoteVersion) : IIntegrationEvent;

/// <summary>A policy was issued: Billing opens the account (if new) and the premium schedule; Documents generates the schedule and certificate.</summary>
[IntegrationEvent("general.GeneralPolicyIssued")]
public sealed record GeneralPolicyIssued(Guid PolicyId, string PolicyNumber, string ProductCode, int ProductVersion, Guid PolicyholderPartyId, Guid PayerPartyId,
    Guid BillingAccountRef, string BillingFrequency, string CollectionMethod, string Currency, decimal AnnualPremium, DateOnly PeriodStart, DateOnly PeriodEnd,
    Guid? RenewalOfId, decimal? SumInsured = null, string? OrgNodeCode = null, decimal? InstalmentLoadingPct = null) : IIntegrationEvent;

/// <summary>
/// An endorsement was applied: Billing amends the schedule from the effective date; Documents generates the endorsement
/// type's documents (<paramref name="DocumentTypes"/>, from general.cfg_endorsement_types).
/// </summary>
[IntegrationEvent("general.GeneralPolicyEndorsed")]
public sealed record GeneralPolicyEndorsed(Guid PolicyId, string PolicyNumber, Guid EndorsementId, string EndorsementNumber, DateOnly EffectiveDate,
    decimal NewAnnualPremium, decimal PremiumDelta, string Currency, string ProductCode, int ProductVersion, IReadOnlyList<string> DocumentTypes,
    decimal? NewSumInsured = null, Guid? PolicyholderPartyId = null) : IIntegrationEvent;

/// <summary>A renewal invitation is due: Documents generates it from <paramref name="TemplateCode"/> (general.cfg_renewal_rules.invitation_template).</summary>
[IntegrationEvent("general.GeneralRenewalInvited")]
public sealed record GeneralRenewalInvited(Guid PolicyId, string PolicyNumber, string ProductCode, int ProductVersion, DateOnly PeriodEnd, string TemplateCode,
    Guid? PolicyholderPartyId = null, decimal? OfferedPremium = null, string? Currency = null) : IIntegrationEvent;

[IntegrationEvent("general.GeneralPolicyRenewed")]
public sealed record GeneralPolicyRenewed(Guid PolicyId, string PolicyNumber, Guid RenewalPolicyId) : IIntegrationEvent;

/// <summary>A policy was cancelled: Billing stops the schedule and raises a credit note for the refund.</summary>
[IntegrationEvent("general.GeneralPolicyCancelled")]
public sealed record GeneralPolicyCancelled(Guid PolicyId, string PolicyNumber, string ReasonCode, DateOnly EffectiveDate, decimal RefundAmount, string Currency,
    string ProductCode, int ProductVersion) : IIntegrationEvent;

[IntegrationEvent("general.GeneralPolicyReinstated")]
public sealed record GeneralPolicyReinstated(Guid PolicyId, string PolicyNumber) : IIntegrationEvent;

[IntegrationEvent("general.GeneralEndorsementApproved")]
public sealed record GeneralEndorsementApproved(Guid EndorsementId, Guid PolicyId, Guid ApprovalId) : IIntegrationEvent;

[IntegrationEvent("general.GeneralClaimRegistered")]
public sealed record GeneralClaimRegistered(Guid ClaimId, string ClaimNumber, Guid PolicyId, DateOnly LossDate, string PerilCode) : IIntegrationEvent;

[IntegrationEvent("general.AssessorAppointed")]
public sealed record AssessorAppointed(Guid ClaimId, Guid AssessorPartyId) : IIntegrationEvent;

/// <summary>The settlement was approved by a second person: the Claims Framework drafts and releases the payment instruction.</summary>
[IntegrationEvent("general.GeneralClaimApproved")]
/// <param name="BankAccountId">The payee's bank account (party.bank_account) the approval covered.</param>
/// <param name="PayeeBankHash">The keyed hash of that account's number, as approved.</param>
/// <param name="HoldReason">PREMIUM_ARREARS: the loss falls in grace without cover and the arrears are unpaid; the payment waits for them (F006).</param>
/// <param name="PremiumOffset">The arrears taken from the settlement by the tenant's rule (F006); <paramref name="Amount"/> is what is paid.</param>
public sealed record GeneralClaimApproved(Guid ClaimId, string ClaimNumber, Guid PayeePartyId, Guid BankAccountId, string PayeeBankHash, decimal Amount, string Currency,
    string ReserveType, Guid ApprovalId, Guid CheckerId, Guid? PolicyId = null, string? HoldReason = null, decimal PremiumOffset = 0m) : IIntegrationEvent;

/// <summary>A policy's arrears were paid (F006): claim payments held for them (PREMIUM_ARREARS) are released.</summary>
[IntegrationEvent("general.GeneralPolicyArrearsCleared")]
public sealed record GeneralPolicyArrearsCleared(Guid PolicyId, string PolicyNumber) : IIntegrationEvent;

/// <summary>Grace ended unpaid and the policy was cancelled from <paramref name="CancelledFrom"/> (F006): its claims for losses from then are declined.</summary>
[IntegrationEvent("general.GeneralPolicyLapsedUnpaid")]
public sealed record GeneralPolicyLapsedUnpaid(Guid PolicyId, string PolicyNumber, DateOnly CancelledFrom, string ClaimDeclineReason) : IIntegrationEvent;

/// <summary>A claim's settlement paid the policy's arrears (F006, the tenant's rule): Billing settles the overdue invoices with it, oldest first.</summary>
[IntegrationEvent("general.GeneralPremiumOffsetFromClaim")]
public sealed record GeneralPremiumOffsetFromClaim(Guid PolicyId, Guid ClaimId, string ClaimNumber, decimal Amount, string Currency) : IIntegrationEvent;

[IntegrationEvent("general.GeneralClaimSettled")]
/// <param name="Amount">The gross the claim paid; with <paramref name="PremiumOffset"/> (OQ-137), the payee received the gross less it.</param>
public sealed record GeneralClaimSettled(Guid ClaimId, string ClaimNumber, decimal Amount, string Currency, DateOnly PaidOn, Guid? PolicyId = null, decimal PremiumOffset = 0m)
    : IIntegrationEvent;

[IntegrationEvent("general.GeneralClaimClosed")]
public sealed record GeneralClaimClosed(Guid ClaimId, string ClaimNumber) : IIntegrationEvent;

[IntegrationEvent("general.LossAssessed")]
public sealed record LossAssessed(Guid AssessmentId, Guid ClaimId, decimal AssessedAmount, decimal ExcessApplied) : IIntegrationEvent;

/// <summary>Money recovered on a claim: the Claims Framework records it as a financial movement (CLM-004).</summary>
[IntegrationEvent("general.RecoveryRecorded")]
public sealed record RecoveryRecorded(Guid RecoveryCaseId, Guid ClaimId, string ClaimNumber, string RecoveryType, decimal Amount, string Currency, string ReserveType)
    : IIntegrationEvent;

/// <summary>The claim was declined, with its decision code (CLM-003); the policyholder is told (communications §6.3).</summary>
[IntegrationEvent("general.GeneralClaimDeclined")]
public sealed record GeneralClaimDeclined(Guid ClaimId, string ClaimNumber, string DecisionCode) : IIntegrationEvent;

/// <summary>A renewal reminder is due (OQ-089): the policy is in force, invited and not renewed, DaysBefore its period ends.</summary>
[IntegrationEvent("general.GeneralRenewalReminderDue")]
public sealed record GeneralRenewalReminderDue(Guid PolicyId, string PolicyNumber, string ProductCode, DateOnly PeriodEnd, int DaysBefore, Guid PolicyholderPartyId,
    string? OrgNodeCode = null) : IIntegrationEvent;

/// <summary>Public query interface: who a General claim's notices go to, the policyholder (communications §6.3); null for an unknown claim.</summary>
public interface IGeneralClaimRecipients
{
    Task<NoticeRecipient?> ForClaim(Guid tenantId, Guid claimId, CancellationToken cancellationToken);
}

/// <summary>The Workflow reference and the rule decisions' subject for a renewal (Wave 4 §4.2).</summary>
public static class GeneralRenewals
{
    public const string BusinessRefType = "GENERAL_RENEWAL";
}

/// <summary>A renewal review asks for its renewal quote to be priced (F018): General creates it and asks Rating.</summary>
[IntegrationEvent("general.GeneralRenewalRatingRequested")]
public sealed record GeneralRenewalRatingRequested(Guid RenewalId, Guid PolicyId) : IIntegrationEvent;

/// <summary>The review refers the renewal to an underwriter (F019), with why: Workflow routes it by authority as a quote referral.</summary>
[IntegrationEvent("general.GeneralRenewalReferred")]
public sealed record GeneralRenewalReferred(Guid RenewalId, Guid PolicyId, string PolicyNumber, string ProductCode, decimal SumInsured, string Reason) : IIntegrationEvent;

/// <summary>The review found the renewal not eligible (F015): a person proposes non-renewal or overrides it.</summary>
[IntegrationEvent("general.GeneralRenewalNotEligible")]
public sealed record GeneralRenewalNotEligible(Guid RenewalId, Guid PolicyId, string PolicyNumber, string ProductCode, decimal SumInsured, string Reason) : IIntegrationEvent;

/// <summary>An underwriter decided a referred or ineligible renewal: its task closes.</summary>
[IntegrationEvent("general.GeneralRenewalDecided")]
public sealed record GeneralRenewalDecided(Guid RenewalId, Guid PolicyId, string PolicyNumber, bool Offered, string DecisionCode) : IIntegrationEvent;

/// <summary>Wave 4 §4.4: the customer accepted the renewal, through STAFF, PORTAL, BROKER or PAYMENT (ON_PAYMENT).</summary>
[IntegrationEvent("general.GeneralRenewalAccepted")]
public sealed record GeneralRenewalAccepted(Guid RenewalId, Guid PolicyId, string PolicyNumber, string Channel, string? ProductCode = null, DateOnly? PeriodEnd = null)
    : IIntegrationEvent;

/// <summary>Wave 4 §4.4: the customer declined the renewal; the policy expires at its period end (and a retention case may open, F026).</summary>
[IntegrationEvent("general.GeneralRenewalDeclined")]
public sealed record GeneralRenewalDeclined(Guid RenewalId, Guid PolicyId, string PolicyNumber, string Channel, string? ProductCode = null, DateOnly? PeriodEnd = null)
    : IIntegrationEvent;

/// <summary>Wave 4 §4.4: a renewal needing acceptance was not accepted (or, ON_PAYMENT, not paid within grace).</summary>
[IntegrationEvent("general.GeneralRenewalLapsed")]
public sealed record GeneralRenewalLapsed(Guid RenewalId, Guid PolicyId, string PolicyNumber, string? ProductCode = null, DateOnly? PeriodEnd = null) : IIntegrationEvent;

/// <summary>
/// B-001: the policy moved to another intermediary from <paramref name="EffectiveDate"/>. Distribution splits the commission on
/// the invoices around the date pro rata (OQ-135); portal access follows the policy's intermediary.
/// </summary>
[IntegrationEvent("general.GeneralPolicyIntermediaryChanged")]
public sealed record GeneralPolicyIntermediaryChanged(Guid PolicyId, string PolicyNumber, string ProductCode, string? PreviousIntermediaryCode, string IntermediaryCode,
    DateOnly EffectiveDate, DateOnly PeriodEnd, string? BillingFrequency) : IIntegrationEvent;

/// <summary>F027: a policy's renewal is on record; it is due in its period end's month.</summary>
[IntegrationEvent("general.GeneralRenewalOpened")]
public sealed record GeneralRenewalOpened(Guid RenewalId, Guid PolicyId, string ProductCode, DateOnly PeriodEnd) : IIntegrationEvent;

/// <summary>F027: the policy renewed, at the renewed premium (the change is renewed less current).</summary>
[IntegrationEvent("general.GeneralRenewalRenewed")]
public sealed record GeneralRenewalRenewed(Guid RenewalId, Guid PolicyId, string ProductCode, DateOnly PeriodEnd, decimal CurrentPremium, decimal RenewedPremium,
    string Currency) : IIntegrationEvent;

/// <summary>F027: the insurer decided not to renew the policy.</summary>
[IntegrationEvent("general.GeneralRenewalNotRenewed")]
public sealed record GeneralRenewalNotRenewed(Guid RenewalId, Guid PolicyId, string ProductCode, DateOnly PeriodEnd) : IIntegrationEvent;

/// <summary>F026: a retention case opened (a cancellation asked for, a renewal declined, a renewal at risk of lapsing): a person owns it in Workflow.</summary>
[IntegrationEvent("general.GeneralRetentionCaseOpened")]
public sealed record GeneralRetentionCaseOpened(Guid CaseId, Guid PolicyId, string PolicyNumber, string ProductCode, string Trigger, Guid? RenewalId) : IIntegrationEvent;

/// <summary>F026, F027: an offer was made on a retention case.</summary>
[IntegrationEvent("general.GeneralRetentionOfferMade")]
public sealed record GeneralRetentionOfferMade(Guid CaseId, Guid OfferId, Guid PolicyId, string ProductCode, string Kind) : IIntegrationEvent;

/// <summary>F026: the customer accepted a retention offer: a discount on the case's renewal changes its terms.</summary>
[IntegrationEvent("general.GeneralRetentionOfferAccepted")]
public sealed record GeneralRetentionOfferAccepted(Guid CaseId, Guid OfferId, Guid PolicyId, string ProductCode, string Kind, decimal? Value, Guid? RenewalId,
    Guid DecidedBy) : IIntegrationEvent;

/// <summary>F026: the retention case closed RETAINED, LOST or WITHDRAWN; its Workflow task closes.</summary>
[IntegrationEvent("general.GeneralRetentionCaseClosed")]
public sealed record GeneralRetentionCaseClosed(Guid CaseId, Guid PolicyId, string ProductCode, string Outcome) : IIntegrationEvent;

/// <summary>OQ-132 (b): cover runs on, pro rata, for a late non-renewal's notice; Billing bills the extra days.</summary>
[IntegrationEvent("general.GeneralCoverExtended")]
public sealed record GeneralCoverExtended(Guid PolicyId, string PolicyNumber, DateOnly PreviousPeriodEnd, DateOnly NewPeriodEnd, decimal ExtraPremium, string Currency)
    : IIntegrationEvent;

/// <summary>The review offered terms (F018): the invitation states them; changed terms are invited with the tenant's longer notice.</summary>
[IntegrationEvent("general.GeneralRenewalOffered")]
public sealed record GeneralRenewalOffered(Guid RenewalId, Guid PolicyId, string PolicyNumber, decimal CurrentPremium, decimal OfferedPremium, string Currency,
    bool ChangedTerms) : IIntegrationEvent;

