/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Integration
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
namespace Insys.Integration.Contracts;

/// <summary>The display points OQ-157 makes mandatory. A claim decision has no step for the member to acknowledge, so its copy goes by the existing notices and nothing is gated.</summary>
public static class DisclosurePoints
{
    public const string Quote = "QUOTE";
    public const string Inception = "INCEPTION";
    public const string Renewal = "RENEWAL";
    public const string ClaimDecision = "CLAIM_DECISION";
}

/// <summary>
/// Public query interface (F060): which disclosures in force at a display point the party hasn't acknowledged, for this business record if one is named. With no text in force at the point, none:
/// the tenant hasn't adopted it, and the step runs as before.
/// </summary>
public interface IDisclosureGate
{
    Task<IReadOnlyList<string>> Outstanding(Guid tenantId, Guid partyId, string displayPoint, string? businessRefType, Guid? businessRefId, DateOnly day, CancellationToken cancellationToken);
}
