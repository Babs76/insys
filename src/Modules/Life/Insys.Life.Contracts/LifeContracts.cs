/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Life
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

namespace Insys.Life.Contracts;

public static class LifePermissions
{
    public const string ProposalRead = "life_proposal.read";
    public const string ProposalCreate = "life_proposal.create";
    public const string ProposalUpdate = "life_proposal.update";
    public const string UnderwritingRead = "life_underwriting.read";
    public const string UnderwritingUpdate = "life_underwriting.update";

    /// <summary>Waiving mandatory evidence is an authority of its own.</summary>
    public const string UnderwritingWaive = "life_underwriting.waive";

    public const string UnderwritingDecide = "life_underwriting.decide";
    public const string PolicyRead = "life_policy.read";
    public const string PolicyCreate = "life_policy.create";
    public const string PolicyUpdate = "life_policy.update";
    public const string LoanRead = "life_loan.read";
    public const string LoanCreate = "life_loan.create";
    public const string LoanUpdate = "life_loan.update";
    public const string ClaimRead = "life_claim.read";
    public const string ClaimCreate = "life_claim.create";
    public const string ClaimUpdate = "life_claim.update";
    public const string ClaimDecide = "life_claim.decide";
    public const string SchemeRead = "life_group_scheme.read";
    public const string SchemeCreate = "life_group_scheme.create";
    public const string SchemeUpdate = "life_group_scheme.update";
}

/// <summary>What Life asks Workflow to approve (ADR-009).</summary>
public static class LifeApprovals
{
    public const string UnderwritingBusinessRefType = "LIFE_UNDERWRITING_CASE";
    public const string UnderwritingApprovalType = "LIFE_UNDERWRITING";
    public const string ClaimBusinessRefType = "LIFE_CLAIM";
    public const string ClaimApprovalType = "LIFE_CLAIM";
}

/// <summary>The subjects Life rates through Rating and checks through Rules.</summary>
public static class LifeSubjects
{
    public const string SourceDomain = "LIFE";
    public const string Proposal = "LIFE_PROPOSAL";

    /// <summary>The subject an underwriting rule set (UW:{product}) is evaluated for.</summary>
    public const string UnderwritingCase = "LIFE_UNDERWRITING_CASE";
}

/// <summary>Document business references for Life (DocumentGenerated.BusinessRefType).</summary>
public static class LifeDocuments
{
    public const string PolicyRefType = "LIFE_POLICY";
}

[IntegrationEvent("life.LifeProposalSubmitted")]
public sealed record LifeProposalSubmitted(Guid ProposalId, string ProposalNumber, string ProductCode, int ProductVersion) : IIntegrationEvent;

[IntegrationEvent("life.LifeProposalWithdrawn")]
public sealed record LifeProposalWithdrawn(Guid ProposalId, string ProposalNumber) : IIntegrationEvent;

[IntegrationEvent("life.LifeProposalAccepted")]
public sealed record LifeProposalAccepted(Guid ProposalId, string ProposalNumber) : IIntegrationEvent;

[IntegrationEvent("life.LifeProposalDeclined")]
public sealed record LifeProposalDeclined(Guid ProposalId, string ProposalNumber, bool Postponed) : IIntegrationEvent;

/// <summary>An underwriting case was opened: the proposal moves to underwriting ("Case opened").</summary>
[IntegrationEvent("life.LifeUnderwritingCaseOpened")]
public sealed record LifeUnderwritingCaseOpened(Guid CaseId, Guid ProposalId) : IIntegrationEvent;

/// <summary>The underwriting decision, with the rule set version the proposal was checked with (LIF-002). Outcome: ACCEPT, DECLINE or POSTPONE.</summary>
[IntegrationEvent("life.LifeUnderwritingDecided")]
public sealed record LifeUnderwritingDecided(Guid CaseId, Guid ProposalId, string Outcome, string DecisionCode, string? RulesetCode, int? RulesetVersion) : IIntegrationEvent;

/// <summary>
/// A policy was issued: Billing opens the account (if new) and the premium schedule at <paramref name="Premium"/> per
/// instalment over the term; Documents generates the policy schedule.
/// </summary>
[IntegrationEvent("life.LifePolicyIssued")]
public sealed record LifePolicyIssued(Guid PolicyId, string PolicyNumber, Guid ProposalId, string ProductCode, int ProductVersion, Guid PolicyholderPartyId,
    Guid PayerPartyId, Guid BillingAccountRef, string BillingFrequency, string CollectionMethod, string Currency, decimal Premium, DateOnly InceptionDate,
    DateOnly? TermEndDate, decimal SumAssured) : IIntegrationEvent;

[IntegrationEvent("life.LifePolicyChanged")]
public sealed record LifePolicyChanged(Guid PolicyId, string PolicyNumber, string ChangeType, DateOnly EffectiveDate) : IIntegrationEvent;

/// <summary>A policy lapsed (LIF-005): Billing stops its schedule from the lapse date; Distribution claws back unearned commission (ADR-014).</summary>
[IntegrationEvent("life.LifePolicyLapsed")]
public sealed record LifePolicyLapsed(Guid PolicyId, string PolicyNumber, DateOnly LapseDate, int MissedPayments, string ProductCode, int ProductVersion, string Currency)
    : IIntegrationEvent;

/// <summary>A lapsed policy was reinstated: Billing resumes its schedule from <paramref name="ReinstatedOn"/> to the end of the term.</summary>
[IntegrationEvent("life.LifePolicyReinstated")]
public sealed record LifePolicyReinstated(Guid PolicyId, string PolicyNumber, DateOnly ReinstatedOn, DateOnly? TermEndDate) : IIntegrationEvent;

[IntegrationEvent("life.PolicyLoanIssued")]
public sealed record PolicyLoanIssued(Guid LoanId, string LoanNumber, Guid PolicyId, decimal Principal, string Currency) : IIntegrationEvent;

[IntegrationEvent("life.PolicyLoanRepaid")]
public sealed record PolicyLoanRepaid(Guid LoanId, string LoanNumber, Guid PolicyId) : IIntegrationEvent;

[IntegrationEvent("life.LifeClaimRegistered")]
public sealed record LifeClaimRegistered(Guid ClaimId, string ClaimNumber, Guid PolicyId, string ClaimType, DateOnly EventDate) : IIntegrationEvent;

/// <param name="BankAccountId">The payee's bank account (party.bank_account) the approval covered; never its details.</param>
/// <param name="BankAccountHash">The keyed hash of that account's number, as approved.</param>
public sealed record LifeClaimPayee(Guid PartyId, Guid BankAccountId, string BankAccountHash, decimal Amount);

/// <summary>
/// A claim was approved by a second person: the Claims Framework drafts and releases one payment instruction per
/// payee; a policy loan the claim settles is offset (<paramref name="LoanOffset"/>).
/// </summary>
[IntegrationEvent("life.LifeClaimApproved")]
public sealed record LifeClaimApproved(Guid ClaimId, string ClaimNumber, Guid PolicyId, decimal Amount, decimal LoanOffset, string Currency, string ReserveType,
    IReadOnlyList<LifeClaimPayee> Payees, Guid ApprovalId, Guid CheckerId) : IIntegrationEvent;

[IntegrationEvent("life.LifeClaimDeclined")]
public sealed record LifeClaimDeclined(Guid ClaimId, string ClaimNumber, string DecisionReason) : IIntegrationEvent;

/// <summary>The claim was paid in full: the policy closes the benefits paid (CloseOnClaim).</summary>
[IntegrationEvent("life.LifeClaimSettled")]
public sealed record LifeClaimSettled(Guid ClaimId, string ClaimNumber, Guid PolicyId, IReadOnlyList<Guid> PolicyBenefitIds, decimal Amount, string Currency, DateOnly PaidOn)
    : IIntegrationEvent;

[IntegrationEvent("life.GroupMemberEnrolled")]
public sealed record GroupMemberEnrolled(Guid SchemeId, string SchemeNumber, Guid MemberId, Guid PartyId, string CategoryCode, decimal CoverAmount, bool EvidenceRequired)
    : IIntegrationEvent;

[IntegrationEvent("life.GroupSchemeRenewed")]
public sealed record GroupSchemeRenewed(Guid SchemeId, string SchemeNumber, DateOnly RenewalDate) : IIntegrationEvent;
