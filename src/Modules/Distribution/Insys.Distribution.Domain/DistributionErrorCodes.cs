/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Distribution
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

namespace Insys.Distribution.Domain;

public static class DistributionErrorCodes
{
    [ErrorCode(422, "error.distribution.plan_not_applicable")]
    public const string PlanNotApplicable = "COMMISSION_PLAN_NOT_APPLICABLE";

    [ErrorCode(409, "error.distribution.nothing_to_accrue")]
    public const string NothingToAccrue = "COMMISSION_NOTHING_TO_ACCRUE";

    [ErrorCode(409, "error.distribution.statement_not_draft")]
    public const string StatementNotDraft = "STATEMENT_NOT_DRAFT";

    [ErrorCode(409, "error.distribution.statement_empty")]
    public const string StatementEmpty = "STATEMENT_EMPTY";

    [ErrorCode(409, "error.distribution.statement_changed")]
    public const string StatementChanged = "STATEMENT_CHANGED";

    [ErrorCode(422, "error.distribution.intermediary_not_payable")]
    public const string IntermediaryNotPayable = "INTERMEDIARY_NOT_PAYABLE";

    [ErrorCode(409, "error.distribution.intermediary_transition")]
    public const string IntermediaryTransition = "INTERMEDIARY_TRANSITION";

    [ErrorCode(422, "error.distribution.intermediary_not_permitted")]
    public const string IntermediaryNotPermitted = "INTERMEDIARY_NOT_PERMITTED";

    [ErrorCode(422, "error.distribution.intermediary_suspended")]
    public const string IntermediarySuspended = "INTERMEDIARY_SUSPENDED";
}
