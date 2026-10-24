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
using Insys.BuildingBlocks.Domain;

namespace Insys.General.Domain;

public static class GeneralErrorCodes
{
    /// <summary>F062 §2: the settlement is more than what remains of the coverage's aggregate claims limit (GEN-008).</summary>
    [ErrorCode(422, "error.general.aggregate_limit_exceeded")]
    public const string AggregateLimitExceeded = "AGGREGATE_LIMIT_EXCEEDED";

    [ErrorCode(422, "error.general.attribute_schema_invalid")]
    public const string AttributeSchemaInvalid = "ATTRIBUTE_SCHEMA_INVALID";

    [ErrorCode(422, "error.general.attributes_invalid")]
    public const string AttributesInvalid = "RISK_ATTRIBUTES_INVALID";

    [ErrorCode(422, "error.general.valuation_basis_required")]
    public const string ValuationBasisRequired = "VALUATION_BASIS_REQUIRED";

    [ErrorCode(422, "error.general.outside_product_limits")]
    public const string OutsideProductLimits = "OUTSIDE_PRODUCT_LIMITS";

    [ErrorCode(422, "error.general.rating_mismatch")]
    public const string RatingMismatch = "RATING_MISMATCH";

    [ErrorCode(409, "error.general.quote_expired")]
    public const string QuoteExpired = "QUOTE_EXPIRED";

    [ErrorCode(409, "error.general.quote_not_accepted")]
    public const string QuoteNotAccepted = "QUOTE_NOT_ACCEPTED";

    [ErrorCode(422, "error.general.product_not_available")]
    public const string ProductNotAvailable = "PRODUCT_NOT_AVAILABLE";

    [ErrorCode(422, "error.general.outside_policy_period")]
    public const string OutsidePolicyPeriod = "OUTSIDE_POLICY_PERIOD";

    [ErrorCode(422, "error.general.rule_not_specified")]
    public const string RuleNotSpecified = "GENERAL_RULE_NOT_SPECIFIED";

    [ErrorCode(409, "error.general.reinstatement_not_permitted")]
    public const string ReinstatementNotPermitted = "REINSTATEMENT_NOT_PERMITTED";

    [ErrorCode(422, "error.general.not_covered")]
    public const string NotCovered = "LOSS_NOT_COVERED";

    [ErrorCode(422, "error.general.endorsement_change_invalid")]
    public const string EndorsementChangeInvalid = "ENDORSEMENT_CHANGE_INVALID";

    [ErrorCode(422, "error.general.rationale_required")]
    public const string RationaleRequired = "DECISION_RATIONALE_REQUIRED";

    [ErrorCode(422, "error.general.nothing_to_settle")]
    public const string NothingToSettle = "NOTHING_TO_SETTLE";

    [ErrorCode(422, "error.general.assessor_not_on_panel")]
    public const string AssessorNotOnPanel = "ASSESSOR_NOT_ON_PANEL";

    [ErrorCode(422, "error.general.code_not_in_force")]
    public const string CodeNotInForce = "GENERAL_CODE_NOT_IN_FORCE";

    [ErrorCode(422, "error.general.renewal_needs_rating")]
    public const string RenewalNeedsRating = "RENEWAL_NEEDS_RATING";

    [ErrorCode(422, "error.general.bank_account_not_payable")]
    public const string BankAccountNotPayable = "BANK_ACCOUNT_NOT_PAYABLE";

    [ErrorCode(409, "error.general.approval_subject_changed")]
    public const string ApprovalSubjectChanged = "SETTLEMENT_SUBJECT_CHANGED";

    [ErrorCode(409, "error.general.assessment_not_accepted")]
    public const string AssessmentNotAccepted = "ASSESSMENT_NOT_ACCEPTED";
}
