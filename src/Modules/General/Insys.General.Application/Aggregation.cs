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
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;
using Insys.General.Contracts;
using Insys.General.Domain;

namespace Insys.General.Application;

/// <summary>A zone and peril's maximum exposure (general.cfg_aggregation_zones_and_limits).</summary>
/// <param name="AlertAtPct">The limit's own alert level, as a percentage; null uses the platform default.</param>
/// <param name="AlertRole">The role reviewing this limit's alerts (OQ-085); null uses General:Aggregation:AlertRole.</param>
public sealed record ZoneLimit(string Zone, string Peril, decimal MaxExposure, string Currency, decimal? AlertAtPct = null, string? AlertRole = null);

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
    /// The default share of a zone and peril's limit (0 to 1) at which AggregationLimitApproached is raised, once a day,
    /// from the exposure snapshot (F062 §1): 80%, decided 24 October 2026. A tenant sets its own on each limit
    /// (alert_at_pct). Empty in configuration, no alert is raised for limits without their own level.
    /// </summary>
    public decimal? AlertAt { get; set; } = 0.8m;

    /// <summary>
    /// The role whose Workflow queue reviews a zone and peril's alerts (GAP-051): UNDERWRITING by default, and a tenant sets
    /// its own on each limit (alert_role, OQ-085 decided 25 October 2026). Empty, alerts are recorded and listed, but no task is opened.
    /// </summary>
    public string? AlertRole { get; set; } = "UNDERWRITING";
}

/// <summary>
/// A zone and peril at or above its alert level on a day (GAP-051, F062 §1), recorded once a day per zone, peril and
/// currency. Capacity limits count every policy in the tenant, so alerts are not narrowed by the reader's F103 scope.
/// </summary>
/// <param name="AlertAtPct">The alert level that applied, in per cent of <paramref name="MaxExposure"/>.</param>
/// <param name="PercentUsed">Exposure as a percentage of the limit, to two decimals.</param>
/// <param name="ZoneReference">The zone and peril's business reference (AGGREGATION_ZONE), which its Workflow review task names.</param>
public sealed record AggregationAlertView(Guid Id, string Zone, string Peril, string Currency, decimal Exposure, decimal MaxExposure, decimal AlertAtPct,
    decimal PercentUsed, DateOnly AsAt, DateTimeOffset RaisedAt, Guid ZoneReference)
{
    public static AggregationAlertView Raise(Guid id, string zone, string peril, string currency, decimal exposure, decimal maxExposure, decimal alertAt,
        DateOnly asAt, DateTimeOffset now) =>
        new(id, zone, peril, currency, exposure, maxExposure, Math.Round(alertAt * 100m, 2), Math.Round(exposure / maxExposure * 100m, 2), asAt, now,
            AggregationZones.Reference(zone, peril));
}

/// <summary>Query ListAggregationAlerts: by zone, peril or zone reference and days, oldest first, by ID with cursor paging.</summary>
public sealed record ListAggregationAlerts(string? Zone, string? Peril, Guid? ZoneReference, DateOnly? From, DateOnly? To, Guid? AfterId, int Limit)
    : IQuery<IReadOnlyList<AggregationAlertView>>;

public sealed record GetAggregationAlert(Guid AlertId) : IQuery<AggregationAlertView>;

public interface IAggregationAlerts
{
    Task<IReadOnlyList<AggregationAlertView>> List(ListAggregationAlerts query, CancellationToken cancellationToken);

    Task<AggregationAlertView?> Get(Guid id, CancellationToken cancellationToken);
}

internal sealed class AggregationAlertHandlers(IAggregationAlerts alerts) :
    IQueryHandler<ListAggregationAlerts, IReadOnlyList<AggregationAlertView>>,
    IQueryHandler<GetAggregationAlert, AggregationAlertView>
{
    public async Task<Result<IReadOnlyList<AggregationAlertView>>> Handle(ListAggregationAlerts query, CancellationToken cancellationToken) =>
        Result.Success(await alerts.List(query, cancellationToken));

    public async Task<Result<AggregationAlertView>> Handle(GetAggregationAlert query, CancellationToken cancellationToken) =>
        await alerts.Get(query.AlertId, cancellationToken) is { } alert ? alert : Error.NotFound(ErrorCodes.NotFound, "The aggregation alert was not found.");
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
