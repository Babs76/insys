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
using Insys.BuildingBlocks.Domain;

namespace Insys.Health.Domain;

public static class HealthErrorCodes
{
    [ErrorCode(422, "error.health.code_not_in_force")]
    public const string CodeNotInForce = "HEALTH_CODE_NOT_IN_FORCE";

    /// <summary>Reading medical information needs a purpose, which is audited with the read (HLT-008).</summary>
    [ErrorCode(422, "error.health.purpose_required")]
    public const string PurposeRequired = "HEALTH_PURPOSE_REQUIRED";

    /// <summary>A provider portal request names a member number (and dependant suffix) that has no current membership.</summary>
    [ErrorCode(422, "error.health.member_not_found")]
    public const string MemberNotFound = "HEALTH_MEMBER_NOT_FOUND";

    /// <summary>The plan's product is not a Health product on sale on the day (addenda/health-plan-products.md §3).</summary>
    [ErrorCode(422, "error.health.product_not_available")]
    public const string ProductNotAvailable = "HEALTH_PRODUCT_NOT_AVAILABLE";

    /// <summary>A plan option names no product, or not the product its plan's other options name (addenda/health-plan-products.md §1).</summary>
    [ErrorCode(422, "error.health.plan_product_invalid")]
    public const string PlanProductInvalid = "HEALTH_PLAN_PRODUCT_INVALID";

    [ErrorCode(422, "error.health.dependant_not_eligible")]
    public const string DependantNotEligible = "DEPENDANT_NOT_ELIGIBLE";

    [ErrorCode(409, "error.health.member_not_covered")]
    public const string MemberNotCovered = "MEMBER_NOT_COVERED";

    [ErrorCode(409, "error.health.limit_exceeded")]
    public const string LimitExceeded = "BENEFIT_LIMIT_EXCEEDED";

    [ErrorCode(422, "error.health.authorisation_above_benefit")]
    public const string AuthorisationAboveBenefit = "AUTHORISATION_ABOVE_BENEFIT";

    [ErrorCode(422, "error.health.claim_invalid")]
    public const string ClaimInvalid = "MEDICAL_CLAIM_INVALID";

    [ErrorCode(409, "error.health.claim_pended")]
    public const string ClaimPended = "MEDICAL_CLAIM_PENDED";

    [ErrorCode(422, "error.health.provider_not_payable")]
    public const string ProviderNotPayable = "PROVIDER_NOT_PAYABLE";

    [ErrorCode(409, "error.health.approval_subject_changed")]
    public const string ApprovalSubjectChanged = "PAYMENT_RUN_SUBJECT_CHANGED";

    [ErrorCode(409, "error.health.run_empty")]
    public const string RunEmpty = "PAYMENT_RUN_EMPTY";

    /// <summary>The provider's statement reference is already recorded (F030).</summary>
    public const string ProviderStatementDuplicate = "PROVIDER_STATEMENT_DUPLICATE";

    /// <summary>The person named does not hold the case manager function (F035).</summary>
    public const string NotACaseManager = "NOT_A_CASE_MANAGER";

    /// <summary>Only a complex case's current manager, or a holder of the clinical function, reads or writes its clinical detail (OQ-143).</summary>
    public const string NotTheCaseManager = "NOT_THE_CASE_MANAGER";

    /// <summary>A coordination note shared with another provider names the member's consent (addendum §4.4).</summary>
    public const string CareConsentRequired = "CARE_CONSENT_REQUIRED";

    [ErrorCode(422, "error.health.rationale_required")]
    public const string RationaleRequired = "HEALTH_RATIONALE_REQUIRED";
}
