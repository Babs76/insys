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
/// The rule every approval shares: the checker must be someone other than the maker (SRS-002).
/// Authority for the amount and type is checked by the Workflow module's Approval aggregate (Phase 2).
/// </summary>
public static class MakerChecker
{
    /// <summary>
    /// The requester of an approval the platform raises with no signed-in user: a provider's result, an event or a job
    /// (BR-109). Any person with authority can decide it, since no person made it; maker-checker still applies to every
    /// approval a person requests.
    /// </summary>
    public static readonly Guid SystemMaker = new("00000000-0000-0000-0000-00000000005e");

    /// <summary>Whether the approval was raised by the platform, not a person.</summary>
    public static bool IsSystem(Guid makerId) => makerId == SystemMaker;

    public static Result EnsureDifferentPeople(string makerId, string checkerId) =>
        string.Equals(makerId, checkerId, StringComparison.OrdinalIgnoreCase)
            ? Error.Forbidden(ErrorCodes.MakerCheckerSamePerson, "The checker must be a different person from the maker.")
            : Result.Success();
}
