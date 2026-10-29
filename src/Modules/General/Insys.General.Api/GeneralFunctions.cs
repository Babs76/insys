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
using Insys.BuildingBlocks.Api.Functions;

namespace Insys.General.Api;

/// <summary>
/// The General module's functions (ADR-026): each screen with the read operations it covers, each action with its
/// operation, by endpoint name, in the sub-domains of docs/specs/INSYS_NAVIGATION.md. Portal screens are declared once
/// per portal. The function catalogue is generated from these.
/// </summary>
internal static class GeneralFunctions
{
    public static readonly IReadOnlyList<FunctionDeclaration> All =
    [
        .. new FunctionMap([Audiences.Staff], Features.General)
        .Screen("GENERAL_AGGREGATION_ALERTS", "RISK_LIMITS", "Daily alerts", "general/aggregation-alerts", "GeneralListAggregationAlerts", "GeneralGetAggregationAlert")
        .Screen("GENERAL_ASSESSMENTS", "GENERAL_CLAIMS", "Loss assessment & excess", "general/assessments", "GeneralListAllAssessments", "GeneralGetAssessment")
        .Action("GENERAL_ASSESSMENT_START", "GENERAL_ASSESSMENTS", "GeneralAssessmentStart")
        .Action("GENERAL_ASSESSMENT_ACCEPT", "GENERAL_ASSESSMENTS", "GeneralAssessmentAccept")
        .Action("GENERAL_ASSESSMENT_REQUEST_FURTHER_WORK", "GENERAL_ASSESSMENTS", "GeneralAssessmentRequestFurtherWork")
        .Action("GENERAL_RECORD_ESTIMATE", "GENERAL_ASSESSMENTS", "GeneralRecordEstimate")
        .Action("GENERAL_SUBMIT_REPORT", "GENERAL_ASSESSMENTS", "GeneralSubmitReport")
        .Screen("GENERAL_CLAIMS", "GENERAL_CLAIMS", "Registration", "general/claims", "GeneralListClaims", "GeneralGetClaim", "GeneralListAssessments")
        .Action("GENERAL_NOTIFY_LOSS", "GENERAL_CLAIMS", "GeneralNotifyLoss")
        .Action("GENERAL_REGISTER_CLAIM", "GENERAL_CLAIMS", "GeneralRegisterClaim")
        .Action("GENERAL_ADD_CLAIM_PARTY", "GENERAL_CLAIMS", "GeneralAddClaimParty")
        .Action("GENERAL_APPOINT_ASSESSOR", "GENERAL_CLAIMS", "GeneralAppointAssessor")
        .Action("GENERAL_REQUEST_SETTLEMENT", "GENERAL_CLAIMS", "GeneralRequestSettlement")
        .Action("GENERAL_DECLINE_CLAIM", "GENERAL_CLAIMS", "GeneralDeclineClaim")
        .Action("GENERAL_CLAIM_CLOSE", "GENERAL_CLAIMS", "GeneralClaimClose")
        .Action("GENERAL_CLAIM_REOPEN", "GENERAL_CLAIMS", "GeneralClaimReopen")
        .Action("GENERAL_OPEN_RECOVERY", "GENERAL_CLAIMS", "GeneralOpenRecovery")
        .Screen("GENERAL_ENDORSEMENTS", "GENERAL_POLICY_ADMIN", "Endorsements (rated, approved)", "general/endorsements", "GeneralListPolicyEndorsements", "GeneralGetEndorsement")
        .Action("GENERAL_SUBMIT_ENDORSEMENT", "GENERAL_ENDORSEMENTS", "GeneralSubmitEndorsement")
        .Screen("GENERAL_POLICIES", "GENERAL_POLICY_ADMIN", "Policy issue", "general/policies", "GeneralListPolicies", "GeneralGetPolicy")
        .Screen("GENERAL_RENEWALS", "GENERAL_RENEWALS", "Risk & claims review", "general/renewals", "GeneralListRenewals", "GeneralGetRenewal")
        .Action("GENERAL_DECIDE_RENEWAL", "GENERAL_RENEWALS", "GeneralDecideRenewal")
        .Action("GENERAL_ISSUE_POLICY", "GENERAL_POLICIES", "GeneralIssuePolicy")
        .Action("GENERAL_RENEW_POLICY", "GENERAL_POLICIES", "GeneralRenewPolicy")
        .Action("GENERAL_CANCEL_POLICY", "GENERAL_POLICIES", "GeneralCancelPolicy")
        .Action("GENERAL_REINSTATE_POLICY", "GENERAL_POLICIES", "GeneralReinstatePolicy")
        .Action("GENERAL_DRAFT_ENDORSEMENT", "GENERAL_POLICIES", "GeneralDraftEndorsement")
        .Screen("GENERAL_PROPOSALS", "GENERAL_POLICY_ADMIN", "Proposal", "general/proposals", "GeneralListProposals", "GeneralGetProposal")
        .Action("GENERAL_SUBMIT_PROPOSAL", "GENERAL_PROPOSALS", "GeneralSubmitProposal")
        .Action("GENERAL_ACCEPT_PROPOSAL", "GENERAL_PROPOSALS", "GeneralAcceptProposal")
        .Screen("GENERAL_QUOTES", "GENERAL_QUOTATION", "Quote & rate", "general/quotes", "GeneralListQuotes", "GeneralGetQuote")
        .Action("GENERAL_CREATE_QUOTE", "GENERAL_QUOTES", "GeneralCreateQuote")
        .Action("GENERAL_QUOTE_ISSUE", "GENERAL_QUOTES", "GeneralQuoteIssue")
        .Action("GENERAL_QUOTE_ACCEPT", "GENERAL_QUOTES", "GeneralQuoteAccept")
        .Action("GENERAL_QUOTE_REJECT", "GENERAL_QUOTES", "GeneralQuoteReject")
        .Action("GENERAL_REFER_QUOTE", "GENERAL_QUOTES", "GeneralReferQuote")
        .Action("GENERAL_DECIDE_REFERRAL", "GENERAL_QUOTES", "GeneralDecideReferral")
        .Screen("GENERAL_RECOVERIES", "GENERAL_RECOVERIES", "Third-party recovery", "general/recoveries", "GeneralListClaimRecoveries", "GeneralGetRecovery")
        .Action("GENERAL_RECOVERY_SEND_DEMAND", "GENERAL_RECOVERIES", "GeneralRecoverySendDemand")
        .Action("GENERAL_RECOVERY_WRITE_OFF", "GENERAL_RECOVERIES", "GeneralRecoveryWriteOff")
        .Action("GENERAL_RECOVERY_CLOSE", "GENERAL_RECOVERIES", "GeneralRecoveryClose")
        .Action("GENERAL_ADD_SALVAGE", "GENERAL_RECOVERIES", "GeneralAddSalvage")
        .Action("GENERAL_DISPOSE_SALVAGE", "GENERAL_RECOVERIES", "GeneralDisposeSalvage")
        .Action("GENERAL_RECORD_RECOVERY", "GENERAL_RECOVERIES", "GeneralRecordRecovery")
        .Screen("GENERAL_RISK_OBJECTS", "GENERAL_RISK_OBJECTS", "Register", "general/risk-objects", "GeneralListRiskObjects", "GeneralGetRiskObject")
        .Action("GENERAL_REGISTER_RISK_OBJECT", "GENERAL_RISK_OBJECTS", "GeneralRegisterRiskObject")
        .Action("GENERAL_UPDATE_RISK_ATTRIBUTES", "GENERAL_RISK_OBJECTS", "GeneralUpdateRiskAttributes")
        .Action("GENERAL_REVALUE_RISK_OBJECT", "GENERAL_RISK_OBJECTS", "GeneralRevalueRiskObject")
        .Action("GENERAL_DEACTIVATE_RISK_OBJECT", "GENERAL_RISK_OBJECTS", "GeneralDeactivateRiskObject")
        .Action("GENERAL_CHANGE_RISK_OBJECT_OWNER", "GENERAL_RISK_OBJECTS", "GeneralChangeRiskObjectOwner")
        .Job("GENERAL_EVENT_PROCESSING", "GENERAL_POLICY_ADMIN", "Background processing")
        .Job("GENERAL_DAILY_JOBS", "GENERAL_POLICY_ADMIN", "Background processing")
            .Functions,
        .. new FunctionMap([Audiences.Customer], Features.CustomerPortal)
        .Screen("CUSTOMER_PORTAL_GENERAL_POLICIES", "CUSTOMER_PORTAL", "Policies", "portal/general/policies", "PortalGeneralListPolicies", "PortalGeneralGetPolicy")
        .Screen("CUSTOMER_PORTAL_GENERAL_CLAIMS", "CUSTOMER_PORTAL", "Claims", "portal/general/claims", "PortalGeneralListClaims", "PortalGeneralGetClaim",
            "PortalGeneralListLossCodes")
        .Action("CUSTOMER_PORTAL_GENERAL_NOTIFY_LOSS", "CUSTOMER_PORTAL_GENERAL_CLAIMS", "PortalGeneralNotifyLoss")
            .Functions,
        .. new FunctionMap([Audiences.Broker], Features.BrokerPortal)
        .Screen("BROKER_PORTAL_GENERAL_POLICIES", "BROKER_PORTAL", "Policies", "portal/general/policies", "PortalGeneralListPolicies", "PortalGeneralGetPolicy")
        .Screen("BROKER_PORTAL_GENERAL_QUOTES", "BROKER_PORTAL", "Quotes", "portal/general/quotes", "PortalGeneralListQuotes", "PortalGeneralGetQuote")
        .Action("BROKER_PORTAL_GENERAL_CREATE_QUOTE", "BROKER_PORTAL_GENERAL_QUOTES", "PortalGeneralCreateQuote")
        .Screen("BROKER_PORTAL_GENERAL_CLAIMS", "BROKER_PORTAL", "Claims", "portal/general/claims", "PortalGeneralListClaims", "PortalGeneralGetClaim")
        .Screen("BROKER_PORTAL_GENERAL_RISK_OBJECTS", "BROKER_PORTAL", "Insured items", "portal/general/risk-objects", "PortalGeneralListRiskObjects",
            "PortalGeneralGetRiskObject")
        .Action("BROKER_PORTAL_GENERAL_REGISTER_RISK_OBJECT", "BROKER_PORTAL_GENERAL_RISK_OBJECTS", "PortalGeneralRegisterRiskObject")
            .Functions,
    ];
}
