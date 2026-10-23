/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Analytics
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
namespace Insys.Analytics.Contracts;

public static class AnalyticsPermissions
{
    public const string ProjectionRead = "analytics.read";
    public const string ProjectionUpdate = "analytics.update";
}

/// <summary>
/// Public query interface: a metric's total over a period from the projections (analytics.cfg_metric_definitions: the
/// metric's source names the projection and measure). Read on Analytics' own connection. TODO(spec): metric formulas
/// are free text and are not evaluated; a metric is the sum of its source measure (OQ-064).
/// </summary>
public interface IProjectionTotals
{
    /// <summary>Null when the metric is not in force or its source is not a projection measure.</summary>
    Task<decimal?> Total(Guid tenantId, string metricCode, DateOnly from, DateOnly to, CancellationToken cancellationToken);
}

/// <summary>The projection measures a metric's source may name.</summary>
public static class ProjectionSources
{
    public const string WrittenPremium = "fact_written_premium.written_premium";
    public const string PolicyCount = "fact_written_premium.policy_count";
    public const string ClaimsIncurred = "fact_claims_incurred.incurred";
    public const string ClaimCount = "fact_claims_incurred.claim_count";

    public static readonly IReadOnlyList<string> All = [WrittenPremium, PolicyCount, ClaimsIncurred, ClaimCount];
}
