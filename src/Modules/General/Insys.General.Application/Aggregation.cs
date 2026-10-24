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
using Insys.General.Domain;

namespace Insys.General.Application;

/// <summary>A zone and peril's maximum exposure (general.cfg_aggregation_zones_and_limits).</summary>
public sealed record ZoneLimit(string Zone, string Peril, decimal MaxExposure, string Currency);

/// <summary>A coverage's aggregate claims limit (general.cfg_aggregate_limit_definitions).</summary>
public sealed record AggregateLimit(string Coverage, string Period, decimal Amount, string? Reinstatement);

/// <summary>A zone and peril over its limit: the exposure in force, what is being added, and the limit.</summary>
public sealed record ZoneLimitExceeded(string Zone, string Peril, decimal Exposure, decimal Added, decimal MaxExposure, string Currency)
{
    public override string ToString() => $"{Zone}/{Peril}: {Exposure:0.00} in force + {Added:0.00} > {MaxExposure:0.00} {Currency}";
}

public sealed class AggregationOptions
{
    /// <summary>
    /// The share of a zone and peril's limit (0 to 1) at which AggregationLimitApproached is raised, once a day, from the
    /// exposure snapshot (F062 §1). A business choice (OQ-076): unset until chosen, and nothing is raised meanwhile.
    /// </summary>
    public decimal? AlertAt { get; set; }
}

/// <summary>
/// Zone and peril exposure limits (F062, docs/specs/addenda/aggregation-limits.md §1). Exposure is measured live, as the
/// sum insured of the risks of issued and in-force policies in the zone, so that two policies issued the same day are
/// both counted. A risk's zone is its risk object's, and the zone's peril is the one general.ref_risk_zones gives it.
/// </summary>
public static class ZoneExposure
{
    /// <summary>The referral reason a quote over a zone limit is referred with (general.underwriting_referral.reason_code holds 15).</summary>
    public const string ReferralReason = "AGG_LIMIT";

    /// <summary>The first zone and peril whose limit the added sums insured would exceed, or null.</summary>
    public static async Task<ZoneLimitExceeded?> FirstExceeded(IGeneralRepository general, IGeneralCodes codes, IReadOnlyList<(Guid RiskObjectId, decimal Added)> risks,
        string currency, DateOnly day, CancellationToken cancellationToken)
    {
        var limits = (await codes.ZoneLimits(day, cancellationToken)).Where(l => l.Currency == currency).ToList();
        if (limits.Count == 0 || risks.Count == 0)
        {
            return null;
        }

        var zones = await general.ZonesOf(risks.Select(r => r.RiskObjectId).Distinct().ToList(), day, cancellationToken);
        foreach (var group in risks.Where(r => r.Added > 0 && zones.ContainsKey(r.RiskObjectId)).GroupBy(r => zones[r.RiskObjectId]).OrderBy(g => g.Key.Zone))
        {
            if (limits.FirstOrDefault(l => l.Zone == group.Key.Zone && l.Peril == group.Key.Peril) is not { } limit)
            {
                continue;
            }

            var added = group.Sum(r => r.Added);
            var exposure = await general.LiveExposure(group.Key.Zone, group.Key.Peril, currency, day, cancellationToken);
            if (exposure + added > limit.MaxExposure)
            {
                return new ZoneLimitExceeded(limit.Zone, limit.Peril, exposure, added, limit.MaxExposure, currency);
            }
        }

        return null;
    }
}

/// <summary>
/// A coverage's aggregate claims limit (F062 §2, GEN-008): its settlements in the period cannot exceed the aggregate.
/// The period is the policy period, or the policy year the loss falls in for ANNUAL.
/// </summary>
public static class ClaimsAggregate
{
    /// <summary>What remains of the coverage's aggregate for a claim, or null when the coverage has none.</summary>
    /// <remarks>
    /// TODO(spec): automatic reinstatement (how often, at what premium) and reinstatement on request are not defined
    /// (OQ-076), so every aggregate stops settlement when used up, as reinstatement NONE does. That errs on the side of
    /// not paying more than the limit; restoring it needs the rule.
    /// </remarks>
    public static async Task<decimal?> Remaining(IGeneralRepository general, IGeneralCodes codes, Guid claimId, Guid policyId, string coverage, DateOnly lossDate,
        DateOnly periodStart, DateOnly periodEnd, DateOnly day, CancellationToken cancellationToken)
    {
        if (await codes.AggregateLimit(coverage, day, cancellationToken) is not { } limit)
        {
            return null;
        }

        var (from, to) = (periodStart, periodEnd);
        if (limit.Period == "ANNUAL")
        {
            var years = 0;
            while (periodStart.AddYears(years + 1) <= lossDate)
            {
                years++;
            }

            (from, to) = (periodStart.AddYears(years), periodStart.AddYears(years + 1).AddDays(-1));
        }

        var used = await general.SettledOnCoverage(policyId, coverage, from, to, claimId, cancellationToken);
        return Math.Max(0m, limit.Amount - used);
    }
}
