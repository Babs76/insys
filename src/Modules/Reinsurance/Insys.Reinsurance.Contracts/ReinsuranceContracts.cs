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
using Insys.BuildingBlocks.Contracts;

namespace Insys.Reinsurance.Contracts;

public static class ReinsurancePermissions
{
    public const string CessionRead = "cession.read";
    public const string RecoveryRead = "ri_recovery.read";
    public const string RecoveryUpdate = "ri_recovery.update";
    public const string BordereauRead = "bordereau.read";
    public const string BordereauCreate = "bordereau.create";
    public const string BordereauUpdate = "bordereau.update";
}

/// <summary>
/// A cession was calculated (or recalculated, superseding <paramref name="SupersedesId"/>) from the arrangement and
/// configuration versions named (REI-002), for a policy (REI-001). Finance posts the ceded premium.
/// </summary>
[IntegrationEvent("reinsurance.CessionCalculated")]
public sealed record CessionCalculated(Guid CessionId, string TreatyCode, string SourceDomain, Guid PolicyRef, decimal CededSumInsured, decimal CededPremium,
    decimal PremiumChange, string Currency, DateOnly PeriodStart, DateOnly PeriodEnd, string RuleCode, int RuleVersion, Guid? SupersedesId) : IIntegrationEvent;

/// <summary>A recovery was billed to the reinsurer (REI-003). Finance posts the recoverable.</summary>
[IntegrationEvent("reinsurance.RecoveryBilled")]
public sealed record RecoveryBilled(Guid RecoveryId, string ClaimNumber, string TreatyCode, string SourceDomain, decimal Amount, string Currency, DateOnly BilledOn)
    : IIntegrationEvent;

/// <summary>A recovery was settled by the reinsurer; any difference from the recoverable stays visible for reconciliation (REI-003).</summary>
[IntegrationEvent("reinsurance.RecoverySettled")]
public sealed record RecoverySettled(Guid RecoveryId, string ClaimNumber, string TreatyCode, string SourceDomain, decimal Recoverable, decimal Amount, string Currency,
    DateOnly SettledOn) : IIntegrationEvent;

[IntegrationEvent("reinsurance.BordereauSent")]
public sealed record BordereauSent(Guid BordereauId, string TreatyCode, string PeriodCode, int RowCount, string? PartnerRef) : IIntegrationEvent;
