/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Reinsurance
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

namespace Insys.Reinsurance.Domain;

public static class ReinsuranceErrorCodes
{
    [ErrorCode(422, "error.reinsurance.treaty_not_supported")]
    public const string TreatyNotSupported = "RI_TREATY_NOT_SUPPORTED";

    [ErrorCode(409, "error.reinsurance.nothing_ceded")]
    public const string NothingCeded = "RI_NOTHING_CEDED";

    [ErrorCode(409, "error.reinsurance.nothing_recoverable")]
    public const string NothingRecoverable = "RI_NOTHING_RECOVERABLE";

    [ErrorCode(422, "error.reinsurance.layout_field_unknown")]
    public const string LayoutFieldUnknown = "RI_LAYOUT_FIELD_UNKNOWN";

    [ErrorCode(409, "error.reinsurance.bordereau_exists")]
    public const string BordereauExists = "RI_BORDEREAU_EXISTS";

    [ErrorCode(422, "error.reinsurance.period_invalid")]
    public const string PeriodInvalid = "RI_PERIOD_INVALID";
}
