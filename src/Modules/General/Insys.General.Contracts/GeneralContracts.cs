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
    public const string EndorsementRead = "general_endorsement.read";
    public const string EndorsementCreate = "general_endorsement.create";
    public const string ClaimRead = "general_claim.read";
    public const string ClaimCreate = "general_claim.create";
    public const string ClaimUpdate = "general_claim.update";
    public const string ClaimDecide = "general_claim.decide";
    public const string AssessmentUpdate = "general_assessment.update";
    public const string RecoveryUpdate = "general_recovery.update";
}

/// <summary>What General asks Workflow to approve (ADR-009).</summary>
public static class GeneralApprovals
{
    public const string EndorsementBusinessRefType = "GENERAL_ENDORSEMENT";
    public const string EndorsementApprovalType = "ENDORSEMENT";
    public const string SettlementBusinessRefType = "GENERAL_CLAIM";
    public const string SettlementApprovalType = "CLAIM_SETTLEMENT";
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
    Guid? RenewalOfId, decimal? SumInsured = null) : IIntegrationEvent;

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
    Guid? PolicyholderPartyId = null) : IIntegrationEvent;

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
public sealed record GeneralClaimApproved(Guid ClaimId, string ClaimNumber, Guid PayeePartyId, Guid BankAccountId, string PayeeBankHash, decimal Amount, string Currency,
    string ReserveType, Guid ApprovalId, Guid CheckerId) : IIntegrationEvent;

[IntegrationEvent("general.GeneralClaimSettled")]
public sealed record GeneralClaimSettled(Guid ClaimId, string ClaimNumber, decimal Amount, string Currency, DateOnly PaidOn, Guid? PolicyId = null) : IIntegrationEvent;

[IntegrationEvent("general.GeneralClaimClosed")]
public sealed record GeneralClaimClosed(Guid ClaimId, string ClaimNumber) : IIntegrationEvent;

[IntegrationEvent("general.LossAssessed")]
public sealed record LossAssessed(Guid AssessmentId, Guid ClaimId, decimal AssessedAmount, decimal ExcessApplied) : IIntegrationEvent;

/// <summary>Money recovered on a claim: the Claims Framework records it as a financial movement (CLM-004).</summary>
[IntegrationEvent("general.RecoveryRecorded")]
public sealed record RecoveryRecorded(Guid RecoveryCaseId, Guid ClaimId, string ClaimNumber, string RecoveryType, decimal Amount, string Currency, string ReserveType)
    : IIntegrationEvent;
