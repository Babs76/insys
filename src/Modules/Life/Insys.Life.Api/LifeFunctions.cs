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
using Insys.BuildingBlocks.Api.Functions;

namespace Insys.Life.Api;

/// <summary>
/// The Life module's functions (ADR-026): each screen with the read operations it covers, each action with its
/// operation, by endpoint name, in the sub-domains of docs/specs/INSYS_NAVIGATION.md. Portal screens are declared once
/// per portal. The function catalogue is generated from these.
/// </summary>
internal static class LifeFunctions
{
    public static readonly IReadOnlyList<FunctionDeclaration> All =
    [
        .. new FunctionMap([Audiences.Staff], Features.Life)
        .Screen("LIFE_CLAIMS", "LIFE_CLAIMS", "Registration & automatic reserve", "life/claims", "LifeListClaims", "LifeGetClaim")
        .Action("LIFE_NOTIFY_CLAIM", "LIFE_CLAIMS", "LifeNotifyClaim")
        .Action("LIFE_REGISTER_CLAIM", "LIFE_CLAIMS", "LifeRegisterClaim")
        .Action("LIFE_ASSESS_CLAIM", "LIFE_CLAIMS", "LifeAssessClaim")
        .Action("LIFE_APPROVE_CLAIM", "LIFE_CLAIMS", "LifeApproveClaim")
        .Action("LIFE_DECLINE_CLAIM", "LIFE_CLAIMS", "LifeDeclineClaim")
        .Action("LIFE_CLOSE_CLAIM", "LIFE_CLAIMS", "LifeCloseClaim")
        .Screen("LIFE_GROUP_SCHEMES", "LIFE_GROUP_SCHEMES", "Scheme setup", "life/group-schemes", "LifeListGroupSchemes", "LifeGetScheme")
        .Action("LIFE_SET_UP_SCHEME", "LIFE_GROUP_SCHEMES", "LifeSetUpScheme")
        .Action("LIFE_ENROL_MEMBER", "LIFE_GROUP_SCHEMES", "LifeEnrolMember")
        .Action("LIFE_CHANGE_MEMBER", "LIFE_GROUP_SCHEMES", "LifeChangeMember")
        .Action("LIFE_ACCEPT_MEMBER_EVIDENCE", "LIFE_GROUP_SCHEMES", "LifeAcceptMemberEvidence")
        .Action("LIFE_REMOVE_MEMBER", "LIFE_GROUP_SCHEMES", "LifeRemoveMember")
        .Action("LIFE_RENEW_SCHEME", "LIFE_GROUP_SCHEMES", "LifeRenewScheme")
        .Action("LIFE_TERMINATE_SCHEME", "LIFE_GROUP_SCHEMES", "LifeTerminateScheme")
        .Screen("LIFE_LOANS", "LIFE_POLICY_VALUES", "Policy loans", "life/loans", "LifeGetLoan")
        .Action("LIFE_REPAY_LOAN", "LIFE_LOANS", "LifeRepayLoan")
        .Screen("LIFE_POLICIES", "LIFE_POLICY_ADMIN", "Issue & in force", "life/policies", "LifeListPolicies", "LifeGetPolicy")
        .Action("LIFE_ISSUE_POLICY", "LIFE_POLICIES", "LifeIssuePolicy")
        .Action("LIFE_CHANGE_BENEFICIARIES", "LIFE_POLICIES", "LifeChangeBeneficiaries")
        .Action("LIFE_ALTER_POLICY", "LIFE_POLICIES", "LifeAlterPolicy")
        .Action("LIFE_REINSTATE_POLICY", "LIFE_POLICIES", "LifeReinstatePolicy")
        .Action("LIFE_POLICY_SURRENDER", "LIFE_POLICIES", "LifePolicySurrender")
        .Action("LIFE_POLICY_MAKE_PAID_UP", "LIFE_POLICIES", "LifePolicyMakePaidUp")
        .Action("LIFE_POLICY_MATURE", "LIFE_POLICIES", "LifePolicyMature")
        .Action("LIFE_ISSUE_LOAN", "LIFE_POLICIES", "LifeIssueLoan")
        .Screen("LIFE_PROPOSALS", "LIFE_NEW_BUSINESS", "Proposal capture", "life/proposals", "LifeListProposals", "LifeGetProposal")
        .Action("LIFE_CAPTURE_PROPOSAL", "LIFE_PROPOSALS", "LifeCaptureProposal")
        .Action("LIFE_SUBMIT_PROPOSAL", "LIFE_PROPOSALS", "LifeSubmitProposal")
        .Action("LIFE_WITHDRAW_PROPOSAL", "LIFE_PROPOSALS", "LifeWithdrawProposal")
        .Screen("LIFE_UNDERWRITING_CASES", "LIFE_UNDERWRITING", "Underwriting cases", "life/underwriting-cases", "LifeGetUnderwritingCase", "LifeGetProposalCase")
        .Action("LIFE_REQUEST_EVIDENCE", "LIFE_UNDERWRITING_CASES", "LifeRequestEvidence")
        .Action("LIFE_RECORD_EVIDENCE", "LIFE_UNDERWRITING_CASES", "LifeRecordEvidence")
        .Action("LIFE_WAIVE_EVIDENCE", "LIFE_UNDERWRITING_CASES", "LifeWaiveEvidence")
        .Action("LIFE_CASE_START_ASSESSMENT", "LIFE_UNDERWRITING_CASES", "LifeCaseStartAssessment")
        .Action("LIFE_CASE_ANSWER_REFERRAL", "LIFE_UNDERWRITING_CASES", "LifeCaseAnswerReferral")
        .Action("LIFE_REFER_CASE", "LIFE_UNDERWRITING_CASES", "LifeReferCase")
        .Action("LIFE_DECIDE_CASE", "LIFE_UNDERWRITING_CASES", "LifeDecideCase")
        .Job("LIFE_EVENT_PROCESSING", "LIFE_POLICY_ADMIN", "Background processing")
        .Job("LIFE_DAILY_JOBS", "LIFE_POLICY_ADMIN", "Background processing")
            .Functions,
        .. new FunctionMap([Audiences.Customer], Features.CustomerPortal)
        .Screen("CUSTOMER_PORTAL_LIFE_POLICIES", "CUSTOMER_PORTAL", "Policies", "portal/life/policies", "PortalLifeListPolicies", "PortalLifeGetPolicy")
        .Screen("CUSTOMER_PORTAL_LIFE_CLAIMS", "CUSTOMER_PORTAL", "Claims", "portal/life/claims", "PortalLifeListClaims", "PortalLifeGetClaim")
            .Functions,
        .. new FunctionMap([Audiences.Broker], Features.BrokerPortal)
        .Screen("BROKER_PORTAL_LIFE_POLICIES", "BROKER_PORTAL", "Policies", "portal/life/policies", "PortalLifeListPolicies", "PortalLifeGetPolicy")
        .Screen("BROKER_PORTAL_LIFE_CLAIMS", "BROKER_PORTAL", "Claims", "portal/life/claims", "PortalLifeListClaims", "PortalLifeGetClaim")
            .Functions,
    ];
}
