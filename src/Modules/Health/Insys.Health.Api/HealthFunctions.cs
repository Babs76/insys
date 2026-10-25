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
using Insys.BuildingBlocks.Api.Functions;

namespace Insys.Health.Api;

/// <summary>
/// The Health module's functions (ADR-026): each screen with the read operations it covers, each action with its
/// operation, by endpoint name, in the sub-domains of docs/specs/INSYS_NAVIGATION.md. Portal screens are declared once
/// per portal. The function catalogue is generated from these.
/// </summary>
internal static class HealthFunctions
{
    public static readonly IReadOnlyList<FunctionDeclaration> All =
    [
        .. new FunctionMap([Audiences.Staff], Features.Health)
        .Screen("HEALTH_CLAIMS", "HEALTH_MEDICAL_CLAIMS", "Validation", "health/claims", "HealthListClaims", "HealthGetClaim")
        .Action("HEALTH_SUBMIT_CLAIM", "HEALTH_CLAIMS", "HealthSubmitClaim")
        .Action("HEALTH_VALIDATE_CLAIM", "HEALTH_CLAIMS", "HealthValidateClaim")
        .Action("HEALTH_ADJUDICATE_CLAIM", "HEALTH_CLAIMS", "HealthAdjudicateClaim")
        .Action("HEALTH_REVIEW_PENDED_CLAIM", "HEALTH_CLAIMS", "HealthReviewPendedClaim")
        .Action("HEALTH_APPROVE_CLAIM", "HEALTH_CLAIMS", "HealthApproveClaim")
        .Action("HEALTH_REJECT_CLAIM", "HEALTH_CLAIMS", "HealthRejectClaim")
        .Action("HEALTH_REVERSE_CLAIM", "HEALTH_CLAIMS", "HealthReverseClaim")
        .Screen("HEALTH_FRAUD_CASES", "HEALTH_FRAUD_UTILISATION", "Fraud cases", "health/fraud-cases", "HealthListFraudCases", "HealthGetFraudCase")
        .Action("HEALTH_INVESTIGATE_FRAUD_CASE", "HEALTH_FRAUD_CASES", "HealthInvestigateFraudCase")
        .Action("HEALTH_CONCLUDE_FRAUD_CASE", "HEALTH_FRAUD_CASES", "HealthConcludeFraudCase")
        .Screen("HEALTH_MEMBERSHIPS", "HEALTH_MEMBERSHIP", "Enrolment", "health/memberships", "HealthListMemberships", "HealthGetMembership")
        .Action("HEALTH_ENROL_MEMBER", "HEALTH_MEMBERSHIPS", "HealthEnrolMember")
        .Action("HEALTH_ADD_DEPENDANT", "HEALTH_MEMBERSHIPS", "HealthAddDependant")
        .Action("HEALTH_REMOVE_DEPENDANT", "HEALTH_MEMBERSHIPS", "HealthRemoveDependant")
        .Action("HEALTH_CHANGE_PLAN", "HEALTH_MEMBERSHIPS", "HealthChangePlan")
        .Action("HEALTH_MEMBERSHIP_SUSPEND", "HEALTH_MEMBERSHIPS", "HealthMembershipSuspend")
        .Action("HEALTH_MEMBERSHIP_REINSTATE", "HEALTH_MEMBERSHIPS", "HealthMembershipReinstate")
        .Action("HEALTH_MEMBERSHIP_TERMINATE", "HEALTH_MEMBERSHIPS", "HealthMembershipTerminate")
        .Screen("HEALTH_BENEFIT_ACCUMULATORS", "HEALTH_MEMBERSHIP", "Benefit accumulators", "health/accumulators", "HealthGetAccumulators")
        .Screen("HEALTH_PAYMENT_RUNS", "HEALTH_PROVIDER_PAYMENTS", "Payment runs (second-person release)", "health/payment-runs", "HealthListRuns", "HealthGetRun")
        .Action("HEALTH_OPEN_RUN", "HEALTH_PAYMENT_RUNS", "HealthOpenRun")
        .Action("HEALTH_CUT_OFF_RUN", "HEALTH_PAYMENT_RUNS", "HealthCutOffRun")
        .Action("HEALTH_CALCULATE_RUN", "HEALTH_PAYMENT_RUNS", "HealthCalculateRun")
        .Action("HEALTH_RELEASE_RUN", "HEALTH_PAYMENT_RUNS", "HealthReleaseRun")
        .Screen("HEALTH_PREAUTHORISATIONS", "HEALTH_ACCESS_TO_CARE", "Preauthorisations", "health/preauthorisations", "HealthListAuthorisations", "HealthGetAuthorisation")
        .Action("HEALTH_REQUEST_AUTHORISATION", "HEALTH_PREAUTHORISATIONS", "HealthRequestAuthorisation")
        .Action("HEALTH_REVIEW_AUTHORISATION", "HEALTH_PREAUTHORISATIONS", "HealthReviewAuthorisation")
        .Action("HEALTH_APPROVE_AUTHORISATION", "HEALTH_PREAUTHORISATIONS", "HealthApproveAuthorisation")
        .Action("HEALTH_REJECT_AUTHORISATION", "HEALTH_PREAUTHORISATIONS", "HealthRejectAuthorisation")
        .Action("HEALTH_MODIFY_AUTHORISATION", "HEALTH_PREAUTHORISATIONS", "HealthModifyAuthorisation")
        .Action("HEALTH_CONFIRM_CONDITIONS_MET", "HEALTH_PREAUTHORISATIONS", "HealthConfirmConditionsMet")
        .Action("HEALTH_CANCEL_AUTHORISATION", "HEALTH_PREAUTHORISATIONS", "HealthCancelAuthorisation")
        .Screen("HEALTH_SCHEMES", "HEALTH_SCHEMES_PLANS", "Schemes", "health/schemes", "HealthGetScheme")
        .Action("HEALTH_REGISTER_SCHEME", "HEALTH_SCHEMES", "HealthRegisterScheme")
        .Action("HEALTH_SCHEME_RENEW", "HEALTH_SCHEMES", "HealthSchemeRenew")
        .Action("HEALTH_SCHEME_TERMINATE", "HEALTH_SCHEMES", "HealthSchemeTerminate")
        .Screen("HEALTH_UTILISATION_CASES", "HEALTH_FRAUD_UTILISATION", "Utilisation cases", "health/utilisation-cases", "HealthListUtilisationCases", "HealthGetUtilisationCase")
        .Action("HEALTH_OPEN_UTILISATION_CASE", "HEALTH_UTILISATION_CASES", "HealthOpenUtilisationCase")
        .Action("HEALTH_REVIEW_UTILISATION_CASE", "HEALTH_UTILISATION_CASES", "HealthReviewUtilisationCase")
        .Action("HEALTH_CONCLUDE_UTILISATION_CASE", "HEALTH_UTILISATION_CASES", "HealthConcludeUtilisationCase")
        .Action("HEALTH_CHECK_ELIGIBILITY", "HEALTH_PREAUTHORISATIONS", "HealthCheckEligibility")
        .Job("HEALTH_EVENT_PROCESSING", "HEALTH_MEMBERSHIP", "Background processing")
        .Job("HEALTH_DAILY_JOBS", "HEALTH_MEMBERSHIP", "Background processing")
            .Functions,
        .. new FunctionMap([Audiences.Customer], Features.CustomerPortal)
        .Screen("CUSTOMER_PORTAL_HEALTH_MEMBERSHIPS", "CUSTOMER_PORTAL", "Membership", "portal/health/memberships", "PortalHealthGetMembership")
        .Screen("CUSTOMER_PORTAL_HEALTH_CLAIMS", "CUSTOMER_PORTAL", "Claims", "portal/health/claims", "PortalHealthListClaims", "PortalHealthGetClaim")
            .Functions,
        .. new FunctionMap([Audiences.Provider], Features.ProviderPortal)
        .Screen("PROVIDER_PORTAL_HEALTH_CLAIMS", "PROVIDER_PORTAL", "Claims", "portal/health/claims", "PortalHealthListClaims", "PortalHealthGetClaim")
        .Action("PROVIDER_PORTAL_HEALTH_SUBMIT_CLAIM", "PROVIDER_PORTAL_HEALTH_CLAIMS", "PortalHealthSubmitClaim")
        .Screen("PROVIDER_PORTAL_HEALTH_PREAUTHORISATIONS", "PROVIDER_PORTAL", "Preauthorisations", "portal/health/preauthorisations", "PortalHealthGetPreauthorisation")
        .Action("PROVIDER_PORTAL_HEALTH_REQUEST_AUTHORISATION", "PROVIDER_PORTAL_HEALTH_PREAUTHORISATIONS", "PortalHealthRequestAuthorisation")
        .Screen("PROVIDER_PORTAL_HEALTH_REMITTANCES", "PROVIDER_PORTAL", "Remittances", "portal/health/remittances", "PortalHealthListRemittances")
            .Functions,
    ];
}
