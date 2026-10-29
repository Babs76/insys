/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Domain
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
namespace Insys.BuildingBlocks.Domain;

/// <summary>
/// The answer to a step a due diligence gate refuses (addenda/due-diligence.md §5, §6). Staff get DUE_DILIGENCE_NOT_CLEARED
/// naming what is outstanding (check types and states, never a hit); a portal user gets the neutral PORTAL_UNDER_REVIEW,
/// which says nothing of why.
/// </summary>
public static class DueDiligenceRefusal
{
    public static Error For(IReadOnlyList<string> outstanding, bool portal) =>
        portal
            ? Error.Validation(ErrorCodes.PortalUnderReview, "We are reviewing the details we hold. We will be in touch, and you can try again once the review is complete.")
            : Error.Validation(ErrorCodes.DueDiligenceNotCleared, $"The party is not cleared by due diligence for this step. Outstanding: {string.Join(", ", outstanding)}.",
                new Dictionary<string, string[]> { ["outstanding"] = [.. outstanding] });
}
