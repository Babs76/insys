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
using Insys.BuildingBlocks.Application.Context;
using System.Text.Json;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.General.Application;
using Insys.General.Domain.Claims;
using Insys.General.Domain.Policies;
using Insys.General.Domain.Underwriting;
using Insys.General.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;

namespace Insys.General.Infrastructure.Persistence;

/// <summary>
/// General's records, each checked against the caller's scope for the endpoint's permission as it is loaded (F103,
/// addenda/access-scopes.md §4): outside it, a read finds nothing (404) and an action is refused (403 OUT_OF_SCOPE).
/// Proposals and endorsements are scoped as their quote and policy. Jobs and consumers are unrestricted.
/// </summary>
internal sealed class GeneralRepository(GeneralDbContext db, IExecutionContext execution) : IGeneralRepository
{
    public Task<RiskObject?> RiskObject(Guid id, CancellationToken cancellationToken) => db.RiskObjects.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);

    public async Task<GeneralQuote?> Quote(Guid id, CancellationToken cancellationToken) =>
        Scoped(await Quotes().SingleOrDefaultAsync(q => q.Id == id, cancellationToken));

    public async Task<GeneralQuote?> LatestQuoteVersion(string quoteNumber, CancellationToken cancellationToken) =>
        Scoped(await Quotes().Where(q => q.QuoteNumber == quoteNumber).OrderByDescending(q => q.QuoteVersion).FirstOrDefaultAsync(cancellationToken));

    public async Task<GeneralProposal?> Proposal(Guid id, CancellationToken cancellationToken) =>
        await OfQuote(await db.Proposals.SingleOrDefaultAsync(p => p.Id == id, cancellationToken), cancellationToken);

    public async Task<GeneralProposal?> ProposalForQuote(Guid quoteId, CancellationToken cancellationToken) =>
        await OfQuote(await db.Proposals.SingleOrDefaultAsync(p => p.QuoteId == quoteId, cancellationToken), cancellationToken);

    public async Task<GeneralPolicy?> Policy(Guid id, CancellationToken cancellationToken) =>
        Scoped(await Policies().SingleOrDefaultAsync(p => p.Id == id, cancellationToken));

    public async Task<GeneralPolicy?> PolicyFromQuote(Guid quoteId, CancellationToken cancellationToken) =>
        Scoped(await Policies().FirstOrDefaultAsync(p => p.QuoteId == quoteId, cancellationToken));

    public async Task<GeneralPolicy?> RenewalOf(Guid policyId, CancellationToken cancellationToken) =>
        Scoped(await Policies().FirstOrDefaultAsync(p => p.RenewalOfId == policyId, cancellationToken));

    public async Task<GeneralEndorsement?> Endorsement(Guid id, CancellationToken cancellationToken)
    {
        var endorsement = await db.Endorsements.SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (endorsement is null)
        {
            return null;
        }

        var owner = await db.Policies.AsNoTracking().Where(p => p.Id == endorsement.PolicyId).Select(p => new { p.ProductCode, p.OrgNodeCode }).SingleAsync(cancellationToken);
        return execution.InScope(endorsement, owner.ProductCode, owner.OrgNodeCode);
    }

    public Task<int> EndorsementCount(Guid policyId, CancellationToken cancellationToken) => db.Endorsements.CountAsync(e => e.PolicyId == policyId, cancellationToken);

    public async Task<GeneralClaim?> Claim(Guid id, CancellationToken cancellationToken) =>
        Scoped(await db.Claims.Include(c => c.Parties).SingleOrDefaultAsync(c => c.Id == id, cancellationToken));

    public async Task<GeneralClaim?> ClaimByNumber(string claimNumber, CancellationToken cancellationToken) =>
        Scoped(await db.Claims.Include(c => c.Parties).SingleOrDefaultAsync(c => c.ClaimNumber == claimNumber, cancellationToken));

    private async Task<GeneralProposal?> OfQuote(GeneralProposal? proposal, CancellationToken cancellationToken)
    {
        if (proposal is null)
        {
            return null;
        }

        var owner = await db.Quotes.AsNoTracking().Where(q => q.Id == proposal.QuoteId).Select(q => new { q.ProductCode, q.OrgNodeCode }).SingleAsync(cancellationToken);
        return execution.InScope(proposal, owner.ProductCode, owner.OrgNodeCode);
    }

    private GeneralQuote? Scoped(GeneralQuote? quote) => execution.InScope(quote, quote?.ProductCode, quote?.OrgNodeCode);

    private GeneralPolicy? Scoped(GeneralPolicy? policy) => execution.InScope(policy, policy?.ProductCode, policy?.OrgNodeCode);

    private GeneralClaim? Scoped(GeneralClaim? claim) => execution.InScope(claim, claim?.ProductCode, claim?.OrgNodeCode);

    public Task<LossAssessment?> Assessment(Guid id, CancellationToken cancellationToken) =>
        db.Assessments.Include(a => a.Estimates).SingleOrDefaultAsync(a => a.Id == id, cancellationToken);

    public async Task<IReadOnlyList<LossAssessment>> Assessments(Guid claimId, CancellationToken cancellationToken)
    {
        var stored = await db.Assessments.Include(a => a.Estimates).Where(a => a.ClaimId == claimId).OrderBy(a => a.Id).ToListAsync(cancellationToken);
        var added = db.ChangeTracker.Entries<LossAssessment>().Where(e => e.State == EntityState.Added && e.Entity.ClaimId == claimId).Select(e => e.Entity);
        return stored.Concat(added).Distinct().ToList();
    }

    public Task<RecoveryCase?> Recovery(Guid id, CancellationToken cancellationToken) =>
        db.Recoveries.Include(r => r.Salvage).SingleOrDefaultAsync(r => r.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Guid>> PoliciesToStart(DateOnly day, int limit, CancellationToken cancellationToken) =>
        await db.Policies.AsNoTracking().Where(p => p.Status == PolicyStatus.Issued && p.PeriodStart <= day && (p.CoverStartsOn == "ISSUE" || p.FirstPaidOn != null)).OrderBy(p => p.Id).Select(p => p.Id).Take(limit)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> PoliciesToExpire(DateOnly day, int limit, CancellationToken cancellationToken) =>
        await db.Policies.AsNoTracking().Where(p => p.Status == PolicyStatus.InForce && p.PeriodEnd < day).OrderBy(p => p.Id).Select(p => p.Id).Take(limit)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> PoliciesWithIntermediaryDue(DateOnly day, int limit, CancellationToken cancellationToken) =>
        await db.Policies.AsNoTracking().Where(p => p.PendingIntermediaryFrom != null && p.PendingIntermediaryFrom <= day && p.Status == PolicyStatus.InForce)
            .OrderBy(p => p.Id).Select(p => p.Id).Take(limit).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> PoliciesPastGrace(DateOnly day, int limit, CancellationToken cancellationToken) =>
        await db.Policies.AsNoTracking().Where(p => p.Status == PolicyStatus.InForce && p.GraceUntil != null && p.GraceUntil < day && p.ArrearsCount > 0)
            .OrderBy(p => p.Id).Select(p => p.Id).Take(limit).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> ClaimsOfPolicy(Guid policyId, CancellationToken cancellationToken) =>
        await db.Claims.AsNoTracking().Where(c => c.PolicyId == policyId).Select(c => c.Id).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> PoliciesOnAccount(Guid billingAccountRef, CancellationToken cancellationToken) =>
        await db.Policies.AsNoTracking().Where(p => p.BillingAccountRef == billingAccountRef && p.Status == PolicyStatus.InForce).Select(p => p.Id)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> PoliciesToInvite(string productCode, DateOnly lastPeriodEnd, int limit, CancellationToken cancellationToken) =>
        await db.Policies.AsNoTracking().Where(p => p.ProductCode == productCode && p.Status == PolicyStatus.InForce && p.RenewalInvitedOn == null
                                                    && p.PeriodEnd <= lastPeriodEnd)
            .OrderBy(p => p.Id).Select(p => p.Id).Take(limit).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> PoliciesToRemind(string productCode, DateOnly periodEnd, int limit, CancellationToken cancellationToken) =>
        await db.Policies.AsNoTracking().Where(p => p.ProductCode == productCode && p.Status == PolicyStatus.InForce && p.RenewalInvitedOn != null
                                                    && p.PeriodEnd == periodEnd)
            .OrderBy(p => p.Id).Select(p => p.Id).Take(limit).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> PoliciesToRenew(string productCode, DateOnly day, int limit, CancellationToken cancellationToken) =>
        await db.Policies.AsNoTracking().Where(p => p.ProductCode == productCode && p.Status == PolicyStatus.InForce && p.PeriodEnd <= day
                                                    && !db.Policies.Any(r => r.RenewalOfId == p.Id))
            .OrderBy(p => p.Id).Select(p => p.Id).Take(limit).ToListAsync(cancellationToken);

    /// <summary>
    /// The exposure snapshot (OQ-066 item 4): the sum insured of the risks attached on the day to policies in force, by the
    /// risk object's zone (UNKNOWN when it has none) and the zone's peril (general.ref_risk_zones), replacing the day's rows.
    /// TODO(spec): the snapshot's key has no currency; one currency per tenant is assumed.
    /// </summary>
    public async Task<int> RefreshExposure(DateOnly day, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM general.exposure_snapshot WHERE as_at = {day}", cancellationToken);
        return await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO general.exposure_snapshot (tenant_id, refreshed_at, as_at, zone, peril_code, sum_insured, currency)
            SELECT p.tenant_id, {now}, {day}, coalesce(o.risk_zone, 'UNKNOWN'), coalesce(z.peril, 'UNKNOWN'), sum(r.sum_insured), min(p.currency)
            FROM general.policy p
            JOIN general.policy_risk r ON r.tenant_id = p.tenant_id AND r.policy_id = p.id
            JOIN general.risk_object o ON o.tenant_id = r.tenant_id AND o.id = r.risk_object_id
            LEFT JOIN LATERAL (
              SELECT left(peril, 10) AS peril FROM general.ref_risk_zones
              WHERE tenant_id = p.tenant_id AND zone_code = o.risk_zone AND record_status IN ('ACTIVE', 'DEPRECATED') AND effective_from <= {day}
                AND (effective_to IS NULL OR effective_to > {day})
              ORDER BY version DESC LIMIT 1) z ON true
            WHERE p.status = 'IN_FORCE' AND p.period_start <= {day} AND p.period_end >= {day} AND r.attached_from <= {day}
              AND (r.detached_on IS NULL OR r.detached_on > {day})
            GROUP BY p.tenant_id, coalesce(o.risk_zone, 'UNKNOWN'), coalesce(z.peril, 'UNKNOWN')
            """, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, (string Zone, string Peril)>> ZonesOf(IReadOnlyCollection<Guid> riskObjectIds, DateOnly day,
        CancellationToken cancellationToken)
    {
        var zones = await db.RiskObjects.AsNoTracking().Where(o => riskObjectIds.Contains(o.Id) && o.RiskZone != null).Select(o => new { o.Id, o.RiskZone })
            .ToListAsync(cancellationToken);
        var codes = zones.Select(z => z.RiskZone!).Distinct().ToList();
        var perils = (await db.Set<RiskZones>().AsNoTracking()
                .InForce(GeneralStaticData.Descriptors.Single(d => d.EntityType == typeof(RiskZones)).Lifecycle, day)
                .Where(z => codes.Contains(z.ZoneCode)).Select(z => new { z.ZoneCode, z.Peril, z.Version }).ToListAsync(cancellationToken))
            .GroupBy(z => z.ZoneCode).ToDictionary(g => g.Key, g => g.MaxBy(z => z.Version)!.Peril);
        return zones.Where(z => perils.ContainsKey(z.RiskZone!)).ToDictionary(z => z.Id, z => (z.RiskZone!, perils[z.RiskZone!]));
    }

    /// <summary>Across the tenant, whatever the caller's scope (F103): a capacity limit counts every policy.</summary>
    public async Task<decimal> LiveExposure(string zone, string peril, string currency, DateOnly day, CancellationToken cancellationToken)
    {
        var inZone = db.RiskObjects.Where(o => o.RiskZone == zone).Select(o => o.Id);
        return await db.Policies.AsNoTracking()
            .Where(p => (p.Status == PolicyStatus.Issued || p.Status == PolicyStatus.InForce) && p.Currency == currency && p.PeriodEnd >= day)
            .SelectMany(p => p.Risks)
            .Where(r => inZone.Contains(r.RiskObjectId) && (r.DetachedOn == null || r.DetachedOn > day))
            .SumAsync(r => r.SumInsured, cancellationToken);
    }

    public async Task<decimal> SettledOnCoverage(Guid policyId, string coverage, DateOnly from, DateOnly to, Guid exceptClaimId, CancellationToken cancellationToken) =>
        await db.Claims.AsNoTracking()
            .Where(c => c.PolicyId == policyId && c.CoverageCode == coverage && c.Id != exceptClaimId && c.LossDate >= from && c.LossDate <= to && c.SettlementAmount != null)
            .SumAsync(c => c.SettlementAmount ?? 0m, cancellationToken);

    public async Task<IReadOnlyList<(string Zone, string Peril, decimal SumInsured, string Currency)>> Snapshot(DateOnly day, CancellationToken cancellationToken) =>
        (await db.Database.SqlQuery<SnapshotRow>(
                $"SELECT zone AS \"Zone\", peril_code AS \"Peril\", sum_insured AS \"SumInsured\", currency AS \"Currency\" FROM general.exposure_snapshot WHERE as_at = {day}")
            .ToListAsync(cancellationToken))
        .Select(r => (r.Zone, r.Peril, r.SumInsured, r.Currency)).ToList();

    public async Task<bool> RecordLimitApproached(AggregationAlertView alert, string? targetRole, CancellationToken cancellationToken)
    {
        if (await db.AggregationAlerts.AnyAsync(a => a.Zone == alert.Zone && a.PerilCode == alert.Peril && a.Currency == alert.Currency && a.AsAt == alert.AsAt,
                cancellationToken))
        {
            return false;
        }

        db.AggregationAlerts.Add(new AggregationAlert
        {
            Id = alert.Id, Zone = alert.Zone, PerilCode = alert.Peril, Currency = alert.Currency, Exposure = alert.Exposure, MaxExposure = alert.MaxExposure,
            AlertAtPct = alert.AlertAtPct, AsAt = alert.AsAt, RaisedAt = alert.RaisedAt,
        });
        db.Publish(new Contracts.AggregationLimitApproached(alert.Zone, alert.Peril, alert.Exposure, alert.MaxExposure, alert.Currency, alert.AsAt, alert.Id,
            alert.AlertAtPct, targetRole), "AggregationAlert", alert.Id, alert.RaisedAt);
        return true;
    }

    private sealed record SnapshotRow(string Zone, string Peril, decimal SumInsured, string Currency);

    public async Task<IReadOnlyList<Guid>> QuotesToExpire(DateOnly day, int limit, CancellationToken cancellationToken) =>
        await db.Quotes.AsNoTracking().Where(q => q.Status == QuoteStatus.Issued && q.ValidUntil < day).OrderBy(q => q.Id).Select(q => q.Id).Take(limit)
            .ToListAsync(cancellationToken);

    public void Add(RiskObject riskObject) => db.RiskObjects.Add(riskObject);

    public async Task AddEffect(PolicyEffect effect, CancellationToken cancellationToken)
    {
        if (!await db.Set<PolicyEffect>().AnyAsync(e => e.EffectType == effect.EffectType && e.RecordId == effect.RecordId, cancellationToken))
        {
            db.Set<PolicyEffect>().Add(effect);
        }
    }

    public async Task<IReadOnlyList<PolicyEffect>> Effects(Guid policyId, CancellationToken cancellationToken) =>
        await db.Set<PolicyEffect>().AsNoTracking().Where(e => e.PolicyId == policyId).ToListAsync(cancellationToken);

    public void Add(GeneralQuote quote) => db.Quotes.Add(quote);

    public void Add(GeneralProposal proposal) => db.Proposals.Add(proposal);

    public void Add(GeneralPolicy policy) => db.Policies.Add(policy);

    public void Add(Domain.Renewals.Renewal renewal) => db.Renewals.Add(renewal);

    public async Task<Domain.Renewals.Renewal?> Renewal(Guid id, CancellationToken cancellationToken) =>
        await db.Renewals.Include(r => r.Checks).SingleOrDefaultAsync(r => r.Id == id, cancellationToken) is { } renewal && await Policy(renewal.PolicyId, cancellationToken) is not null
            ? renewal
            : null;

    public async Task<Domain.Renewals.Renewal?> RenewalOfPeriod(Guid policyId, DateOnly periodEnd, CancellationToken cancellationToken) =>
        await db.Renewals.Include(r => r.Checks).SingleOrDefaultAsync(r => r.PolicyId == policyId && r.PeriodEnd == periodEnd, cancellationToken);

    public async Task<Domain.Renewals.Renewal?> LatestRenewal(Guid policyId, CancellationToken cancellationToken) =>
        await db.Renewals.Include(r => r.Checks).Where(r => r.PolicyId == policyId).OrderByDescending(r => r.PeriodEnd).FirstOrDefaultAsync(cancellationToken);

    public async Task<Domain.Renewals.Renewal?> RenewalIssuedAs(Guid renewalPolicyId, CancellationToken cancellationToken) =>
        await db.Renewals.Include(r => r.Checks).SingleOrDefaultAsync(r => r.RenewalPolicyId == renewalPolicyId, cancellationToken);

    public async Task<IReadOnlyList<Guid>> RenewalsAtLapseRisk(DateOnly endsBy, int limit, CancellationToken cancellationToken) =>
        await db.Renewals.AsNoTracking().Where(r => r.Status == Domain.Renewals.RenewalStatus.Offered && r.NeedsAcceptance && r.PeriodEnd <= endsBy
                                                     && !db.RetentionCases.Any(c => c.PolicyId == r.PolicyId && c.Status == Domain.Retention.RetentionCase.Open))
            .OrderBy(r => r.Id).Select(r => r.Id).Take(limit).ToListAsync(cancellationToken);

    public void Add(Domain.Retention.RetentionCase retentionCase) => db.RetentionCases.Add(retentionCase);

    public async Task<Domain.Retention.RetentionCase?> RetentionCase(Guid id, CancellationToken cancellationToken) =>
        await db.RetentionCases.Include(c => c.Offers).SingleOrDefaultAsync(c => c.Id == id, cancellationToken) is { } found && await Policy(found.PolicyId, cancellationToken) is not null
            ? found
            : null;

    public async Task<Domain.Retention.RetentionCase?> OpenRetentionCase(Guid policyId, CancellationToken cancellationToken) =>
        await db.RetentionCases.Include(c => c.Offers).SingleOrDefaultAsync(c => c.PolicyId == policyId && c.Status == Domain.Retention.RetentionCase.Open, cancellationToken);

    public async Task<IReadOnlyList<Domain.Retention.RetentionCase>> RetentionCases(string? status, Guid? policyId, Guid? beforeId, int limit,
        CancellationToken cancellationToken)
    {
        var scoped = db.Policies.InScope(execution.CurrentScope, p => p.ProductCode, p => p.OrgNodeCode).Select(p => p.Id);
        var cases = db.RetentionCases.AsNoTracking().Include(c => c.Offers).Where(c => scoped.Contains(c.PolicyId));
        if (status is not null)
        {
            cases = cases.Where(c => c.Status == status);
        }

        if (policyId is { } policy)
        {
            cases = cases.Where(c => c.PolicyId == policy);
        }

        if (beforeId is { } before)
        {
            cases = cases.Where(c => c.Id.CompareTo(before) < 0);
        }

        return await cases.OrderByDescending(c => c.Id).Take(limit).ToListAsync(cancellationToken);
    }

    public async Task<int> RetentionOfferUses(Guid policyId, string offerCode, CancellationToken cancellationToken) =>
        await db.RetentionCases.Where(c => c.PolicyId == policyId).SelectMany(c => c.Offers).CountAsync(o => o.OfferCode == offerCode, cancellationToken);

    public async Task<IReadOnlyList<Domain.Renewals.Renewal>> Renewals(Domain.Renewals.RenewalStatus? status, Guid? policyId, Guid? beforeId, int limit,
        CancellationToken cancellationToken)
    {
        var listed = new List<Domain.Renewals.Renewal>();
        foreach (var renewal in await db.Renewals.AsNoTracking().Include(r => r.Checks)
                     .Where(r => (status == null || r.Status == status) && (policyId == null || r.PolicyId == policyId) && (beforeId == null || r.Id < beforeId))
                     .OrderByDescending(r => r.Id).Take(limit).ToListAsync(cancellationToken))
        {
            // F103: a renewal is listed only where its policy is in the caller's scope.
            if (await Policy(renewal.PolicyId, cancellationToken) is not null)
            {
                listed.Add(renewal);
            }
        }

        return listed;
    }

    public async Task<IReadOnlyList<Guid>> PoliciesToReview(string productCode, DateOnly lastPeriodEnd, int limit, CancellationToken cancellationToken) =>
        await db.Policies.AsNoTracking().Where(p => p.ProductCode == productCode && p.Status == PolicyStatus.InForce && p.PeriodEnd <= lastPeriodEnd
                                                    && !db.Renewals.Any(r => r.PolicyId == p.Id && r.PeriodEnd == p.PeriodEnd))
            .OrderBy(p => p.Id).Select(p => p.Id).Take(limit).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ClaimHistoryItem>> ClaimHistory(IReadOnlyCollection<Guid> policyIds, CancellationToken cancellationToken) =>
        (await db.Claims.AsNoTracking().Where(c => policyIds.Contains(c.PolicyId))
            .Select(c => new { c.Id, c.PolicyId, c.LossDate, c.Status, c.SettlementAmount }).ToListAsync(cancellationToken))
        .Select(c => new ClaimHistoryItem(c.Id, c.PolicyId, c.LossDate, c.Status,
            c.Status is Domain.Claims.ClaimStatus.Settled or Domain.Claims.ClaimStatus.Closed ? c.SettlementAmount ?? 0m : 0m)).ToList();

    public void Add(GeneralEndorsement endorsement) => db.Endorsements.Add(endorsement);

    public void Add(GeneralClaim claim) => db.Claims.Add(claim);

    public void Add(LossAssessment assessment) => db.Assessments.Add(assessment);

    public void Add(RecoveryCase recovery) => db.Recoveries.Add(recovery);

    private IQueryable<GeneralQuote> Quotes() =>
        db.Quotes.Include(q => q.Risks).ThenInclude(r => r.Coverages).Include(q => q.Referrals).AsSplitQuery();

    private IQueryable<GeneralPolicy> Policies() =>
        db.Policies.Include(p => p.Risks).ThenInclude(r => r.Coverages).Include(p => p.Cancellations).AsSplitQuery();
}

internal sealed class GeneralCodes(GeneralDbContext db) : IGeneralCodes
{
    public async Task<IReadOnlyList<ZoneLimit>> ZoneLimits(DateOnly day, CancellationToken cancellationToken) =>
        await InForce<AggregationZonesAndLimits>(day).Select(l => new ZoneLimit(l.Zone, l.Peril, l.MaxExposure, l.Currency, l.AlertAtPct, l.AlertRole)).ToListAsync(cancellationToken);

    public async Task<AggregateLimit?> AggregateLimit(string coverage, DateOnly day, CancellationToken cancellationToken) =>
        await InForce<AggregateLimitDefinitions>(day).Where(l => l.Coverage == coverage).OrderByDescending(l => l.Version)
            .Select(l => new AggregateLimit(l.Coverage, l.Period, l.AggregateAmount, l.Reinstatement)).FirstOrDefaultAsync(cancellationToken);

    public async Task<RiskObjectTypeDefinition?> RiskObjectType(string objectType, DateOnly day, CancellationToken cancellationToken)
    {
        var row = await InForce<RiskObjectTypesAndAttributeSchemas>(day).Where(t => t.ObjectType == objectType).OrderByDescending(t => t.Version).FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : new RiskObjectTypeDefinition(row.ObjectType, row.Version, Strings(row.Attributes), row.Validation is { } v ? Strings(v) : []);
    }

    public async Task<(IReadOnlyList<(string Code, string Label)> Perils, IReadOnlyList<(string Code, string Peril, string Label)> Causes)> LossCodes(string line,
        DateOnly day, CancellationToken cancellationToken)
    {
        var perils = (await InForce<PerilCodes>(day).Where(p => p.Line == line).Select(p => new { p.PerilCode, p.Label }).ToListAsync(cancellationToken))
            .OrderBy(p => p.PerilCode, StringComparer.Ordinal).Select(p => (p.PerilCode, p.Label)).ToList();
        var codes = perils.Select(p => p.PerilCode).ToList();
        var causes = (await InForce<LossCauseCodes>(day).Where(c => codes.Contains(c.Peril)).Select(c => new { c.CauseCode, c.Peril, c.Label })
                .ToListAsync(cancellationToken))
            .OrderBy(c => c.CauseCode, StringComparer.Ordinal).Select(c => (c.CauseCode, c.Peril, c.Label)).ToList();
        return (perils, causes);
    }

    public async Task<decimal?> FrequencyLoading(string productCode, string frequency, DateOnly day, CancellationToken cancellationToken) =>
        await InForce<ProductBillingFrequencies>(day).Where(f => f.ProductCode == productCode && f.Frequency == frequency)
            .Select(f => (decimal?)(f.InstalmentLoadingPct ?? 0m)).FirstOrDefaultAsync(cancellationToken);

    public async Task<int?> QuoteValidityDays(string productCode, DateOnly day, CancellationToken cancellationToken) =>
        await InForce<QuoteValidityPeriods>(day).Where(q => q.ProductCode == productCode).Select(q => (int?)q.DaysValid).FirstOrDefaultAsync(cancellationToken);

    public async Task<string?> PolicyNumberPrefix(DateOnly day, CancellationToken cancellationToken) =>
        (await InForce<PolicyNumberingSchemes>(day).ToListAsync(cancellationToken))
        .Where(s => s.ProductLine.Equals("GENERAL", StringComparison.OrdinalIgnoreCase)).Select(s => s.Prefix).FirstOrDefault();

    public async Task<CancellationRule?> CancellationRule(string reasonCode, DateOnly day, CancellationToken cancellationToken)
    {
        if (await InForce<CancellationReasonsAndRefundRules>(day).Where(r => r.ReasonCode == reasonCode).FirstOrDefaultAsync(cancellationToken) is not { } reason)
        {
            return null;
        }

        var scale = reason.ShortPeriodScale is { } scaleCode
            ? await InForce<ShortPeriodScales>(day).Where(b => b.ScaleCode == scaleCode).Select(b => new ShortPeriodBand(b.DaysInForceTo, b.PercentRetained))
                .ToListAsync(cancellationToken)
            : null;
        return new CancellationRule(reason.ReasonCode, reason.Initiator, reason.RefundBasis, reason.Fee ?? 0, scale);
    }

    public async Task<GraceTerms?> GraceTerms(string productCode, string frequency, DateOnly day, CancellationToken cancellationToken) =>
        await InForce<GracePeriodRules>(day).Where(r => r.ProductCode == productCode && r.Frequency == frequency)
            .Select(r => new GraceTerms(r.GraceDays, r.CoverDuringGrace, r.CancellationReason, r.ClaimDeclineReason, r.DeductArrearsFromClaims ?? false))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<EndorsementType?> EndorsementType(string code, DateOnly day, CancellationToken cancellationToken) =>
        await InForce<EndorsementTypes>(day).Where(t => t.Code == code)
            .Select(t => new EndorsementType(t.Code, t.PremiumMethod, t.Documents, t.RequiresUnderwriting ?? false)).FirstOrDefaultAsync(cancellationToken);

    public async Task<RenewalRule?> RenewalRule(string productCode, DateOnly day, CancellationToken cancellationToken) =>
        await InForce<RenewalRules>(day).Where(r => r.ProductCode == productCode)
            .Select(r => new RenewalRule(r.ProductCode, r.NoticeDays, r.AutoRenew, r.ReRating, r.InvitationTemplate, r.ReminderDays, r.Review ?? false,
                r.Acceptance ?? Domain.Renewals.RenewalAcceptance.Automatic))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<RenewalRule>> RenewalRules(DateOnly day, CancellationToken cancellationToken) =>
        await InForce<RenewalRules>(day).OrderBy(r => r.ProductCode)
            .Select(r => new RenewalRule(r.ProductCode, r.NoticeDays, r.AutoRenew, r.ReRating, r.InvitationTemplate, r.ReminderDays, r.Review ?? false,
                r.Acceptance ?? Domain.Renewals.RenewalAcceptance.Automatic))
            .ToListAsync(cancellationToken);

    public async Task<RenewalReviewRule?> RenewalReviewRule(string productCode, DateOnly day, CancellationToken cancellationToken) =>
        await InForce<RenewalReviewRules>(day).Where(r => r.ProductCode == productCode)
            .Select(r => new RenewalReviewRule(r.ProductCode, r.EligibilityRuleset, r.UnderwritingRules ?? true, r.ClaimsLookbackYears, r.ReviewDaysBeforeInvitation))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<decimal> ClaimsFactor(string productCode, int claimsCount, decimal? lossRatio, DateOnly day, CancellationToken cancellationToken)
    {
        var bands = await InForce<ClaimsExperienceFactors>(day).Where(f => f.ProductCode == productCode).OrderBy(f => f.ClaimsCountFrom).ThenBy(f => f.Code)
            .ToListAsync(cancellationToken);
        return bands.FirstOrDefault(f => claimsCount >= f.ClaimsCountFrom && (f.ClaimsCountTo is null || claimsCount <= f.ClaimsCountTo)
                                         && (f.LossRatioFrom is null || (lossRatio ?? 0) >= f.LossRatioFrom) && (f.LossRatioTo is null || (lossRatio ?? 0) <= f.LossRatioTo))
            ?.Factor ?? 1m;
    }

    public async Task<Domain.Retention.RetentionOfferTerms?> RetentionOffer(string offerCode, DateOnly day, CancellationToken cancellationToken) =>
        await InForce<RetentionOffers>(day).Where(o => o.OfferCode == offerCode)
            .Select(o => new Domain.Retention.RetentionOfferTerms(o.OfferCode, o.ProductCode, o.Kind, o.Value, o.MaxUsesPerPolicy, o.AuthorityRole))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<Application.NonRenewalReason?> NonRenewalReason(string reasonCode, DateOnly day, CancellationToken cancellationToken) =>
        await InForce<NonRenewalReasons>(day).Where(r => r.ReasonCode == reasonCode)
            .Select(r => new Application.NonRenewalReason(r.ReasonCode, r.NoticeDays, r.CustomerWording, r.RequiresApproval)).FirstOrDefaultAsync(cancellationToken);

    public async Task<Application.RenewalTermsSettings> RenewalSettings(DateOnly day, CancellationToken cancellationToken) =>
        await InForce<StaticData.RenewalSettings>(day).Select(s => new Application.RenewalTermsSettings(s.WorseTermsPct, s.ChangedTermsNoticeDays, s.LapseRiskDays))
            .FirstOrDefaultAsync(cancellationToken) ?? Application.RenewalTermsSettings.Default;

    public Task<bool> Exists(GeneralCode kind, string code, DateOnly day, CancellationToken cancellationToken) => kind switch
    {
        GeneralCode.ReferralReason => InForce<ReferralReasonCodes>(day).AnyAsync(r => r.Code == code, cancellationToken),
        GeneralCode.UnderwritingDecision => InForce<UnderwritingDecisionCodes>(day).AnyAsync(r => r.Code == code, cancellationToken),
        GeneralCode.LossCause => InForce<LossCauseCodes>(day).AnyAsync(r => r.CauseCode == code, cancellationToken),
        GeneralCode.Peril => InForce<PerilCodes>(day).AnyAsync(r => r.PerilCode == code, cancellationToken),
        GeneralCode.CatastropheEvent => InForce<CatastropheEventRegister>(day).AnyAsync(r => r.EventCode == code, cancellationToken),
        GeneralCode.RecoveryType => InForce<RecoveryTypes>(day).AnyAsync(r => r.Code == code, cancellationToken),
        GeneralCode.ValuationBasis => InForce<ValuationBases>(day).AnyAsync(r => r.Code == code, cancellationToken),
        GeneralCode.ClaimPartyRole => InForce<ClaimPartyRoles>(day).AnyAsync(r => r.Code == code, cancellationToken),
        _ => InForce<SalvageDisposalMethods>(day).AnyAsync(r => r.Code == code, cancellationToken),
    };

    public async Task<bool> OnPanel(Guid partyId, string line, bool repairer, DateOnly day, CancellationToken cancellationToken)
    {
        var party = partyId.ToString();
        var panelType = repairer ? "REPAIRER" : "ASSESSOR";
        return await InForce<RepairerAndAssessorPanels>(day).AnyAsync(p => p.Party == party && p.PanelType == panelType && p.Status == "ACTIVE" && p.ApprovedLines.Contains(line),
                   cancellationToken)
               || (!repairer && await InForce<AdjusterAndSurveyorPanels>(day).AnyAsync(p => p.Party == party && p.Status == "ACTIVE" && p.Lines.Contains(line), cancellationToken));
    }

    /// <summary>An attribute schema or validation list: a JSON array of strings (name:Type:Y|N, or an expression).</summary>
    private static string[] Strings(JsonElement json) =>
        json.ValueKind == JsonValueKind.Array ? json.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToArray() : [];

    private IQueryable<T> InForce<T>(DateOnly day) where T : StaticRecord =>
        db.Set<T>().AsNoTracking().InForce(GeneralStaticData.Descriptors.Single(d => d.EntityType == typeof(T)).Lifecycle, day);
}

/// <summary>The merge values of CLAIM_DECISION_RIGHTS (OQ-102): business references and amounts, invariant, and the reason's customer wording.</summary>
internal static class DecisionRightsMerge
{
    public static IReadOnlyDictionary<string, string> Of(string claimNumber, DateOnly decidedOn, string? reasons, decimal amount, decimal claimed, string currency)
    {
        var merge = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["claim_number"] = claimNumber,
            ["decision_date"] = decidedOn.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            ["amount"] = amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            ["claimed_amount"] = claimed.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            ["currency"] = currency,
        };
        if (!string.IsNullOrWhiteSpace(reasons))
        {
            merge["decision_reasons"] = reasons;
        }

        return merge;
    }
}

internal sealed class GeneralEventMapper : IDomainEventMapper
{
    public IIntegrationEvent? Map(IDomainEvent domainEvent) => domainEvent switch
    {
        GeneralRiskObjectRegistered e => new Contracts.GeneralRiskObjectRegistered(e.RiskObjectId, e.ObjectType),
        GeneralQuoteIssued e => new Contracts.GeneralQuoteIssued(e.QuoteId, e.QuoteNumber, e.QuoteVersion, e.ProductCode, e.ProductVersion, e.Premium, e.Currency, e.ValidUntil),
        GeneralQuoteAccepted e => new Contracts.GeneralQuoteAccepted(e.QuoteId, e.QuoteNumber, e.QuoteVersion, e.ProposerPartyId),
        GeneralQuoteExpired e => new Contracts.GeneralQuoteExpired(e.QuoteId, e.QuoteNumber, e.QuoteVersion),
        GeneralQuoteDeclined e => new Contracts.GeneralQuoteDeclined(e.QuoteId, e.QuoteNumber, e.QuoteVersion, e.DecisionCode),
        GeneralQuoteReferred e => new Contracts.GeneralQuoteReferred(e.QuoteId, e.QuoteNumber, e.QuoteVersion, e.ProductCode, e.ReasonCode, e.SumInsured, e.Currency),
        GeneralQuoteReferralDecided e => new Contracts.GeneralQuoteReferralDecided(e.QuoteId, e.QuoteNumber, e.QuoteVersion, e.Approved, e.DecisionCode),
        GeneralQuotePricingRequested e => new Contracts.GeneralQuotePricingRequested(e.QuoteId, e.QuoteNumber, e.QuoteVersion, e.ProductCode, e.ProductVersion, e.Currency,
            QuotePricing.Frequency, e.RatingDate, System.Text.Json.JsonDocument.Parse(e.ValuesJson).RootElement.Clone()),
        GeneralPolicyIssued e => new Contracts.GeneralPolicyIssued(e.PolicyId, e.PolicyNumber, e.ProductCode, e.ProductVersion, e.PolicyholderPartyId, e.Billing.PayerPartyId,
            e.Billing.BillingAccountRef, e.Billing.Frequency, e.Billing.CollectionMethod, e.Currency, e.AnnualPremium, e.PeriodStart, e.PeriodEnd, e.RenewalOfId,
            e.SumInsured, e.OrgNodeCode, e.InstalmentLoadingPct),
        GeneralPolicyEndorsed e => new Contracts.GeneralPolicyEndorsed(e.PolicyId, e.PolicyNumber, e.EndorsementId, e.EndorsementNumber, e.EffectiveDate, e.NewAnnualPremium,
            e.PremiumDelta, e.Currency, e.ProductCode, e.ProductVersion, e.DocumentTypes, e.NewSumInsured, e.PolicyholderPartyId),
        GeneralPolicyRenewed e => new Contracts.GeneralPolicyRenewed(e.PolicyId, e.PolicyNumber, e.RenewalPolicyId),
        GeneralRenewalReminderDue e => new Contracts.GeneralRenewalReminderDue(e.PolicyId, e.PolicyNumber, e.ProductCode, e.PeriodEnd, e.DaysBefore, e.PolicyholderPartyId,
            e.OrgNodeCode),
        Domain.Claims.GeneralClaimDeclined e => new Contracts.GeneralClaimDeclined(e.ClaimId, e.ClaimNumber, e.DecisionCode),
        GeneralClaimDecisionRightsDue e => new Communications.Contracts.NotificationRequested(Communications.Contracts.NotificationTypes.ClaimDecisionRights,
            e.RecipientPartyId, "GENERAL_CLAIM", e.ClaimId, DecisionRightsMerge.Of(e.ClaimNumber, e.DecidedOn, e.DecisionReasons, e.Amount, e.ClaimedAmount, e.Currency),
            e.ProductCode, e.OrgNodeCode),
        GeneralRenewalInvited e => new Contracts.GeneralRenewalInvited(e.PolicyId, e.PolicyNumber, e.ProductCode, e.ProductVersion, e.PeriodEnd, e.TemplateCode,
            e.PolicyholderPartyId, e.OfferedPremium, e.Currency),
        // Wave 4 §4.2: the renewal review asks Rules and Rating, and tells Workflow what a person must decide.
        Domain.Renewals.RenewalRuleEvaluationRequested e => new Rules.Contracts.RuleEvaluationRequested(e.Kind, e.Code, Domain.Renewals.RenewalSubjects.SubjectType,
            e.RenewalId, System.Text.Json.JsonSerializer.SerializeToElement(e.Inputs)),
        Domain.Renewals.RenewalRatingRequested e => new Contracts.GeneralRenewalRatingRequested(e.RenewalId, e.PolicyId),
        Domain.Renewals.GeneralRenewalReferred e => new Contracts.GeneralRenewalReferred(e.RenewalId, e.PolicyId, e.PolicyNumber, e.ProductCode, e.SumInsured, e.Reason),
        Domain.Renewals.GeneralRenewalNotEligible e => new Contracts.GeneralRenewalNotEligible(e.RenewalId, e.PolicyId, e.PolicyNumber, e.ProductCode, e.SumInsured, e.Reason),
        Domain.Renewals.GeneralRenewalDecided e => new Contracts.GeneralRenewalDecided(e.RenewalId, e.PolicyId, e.PolicyNumber, e.Offered, e.DecisionCode),
        Domain.Renewals.GeneralRenewalOffered e => new Contracts.GeneralRenewalOffered(e.RenewalId, e.PolicyId, e.PolicyNumber, e.CurrentPremium, e.OfferedPremium, e.Currency,
            e.ChangedTerms),
        // Wave 4 §4.3, §4.4: the customer's answer, a lapse, an insurer's non-renewal (its approval, and its notice) and the cover a late notice extends.
        Domain.Renewals.GeneralRenewalAccepted e => new Contracts.GeneralRenewalAccepted(e.RenewalId, e.PolicyId, e.PolicyNumber, e.Channel, e.ProductCode, e.PeriodEnd),
        Domain.Renewals.GeneralRenewalDeclined e => new Contracts.GeneralRenewalDeclined(e.RenewalId, e.PolicyId, e.PolicyNumber, e.Channel, e.ProductCode, e.PeriodEnd),
        Domain.Renewals.GeneralRenewalLapsed e => new Contracts.GeneralRenewalLapsed(e.RenewalId, e.PolicyId, e.PolicyNumber, e.ProductCode, e.PeriodEnd),
        // F026, F027: the renewal figures and retention.
        Domain.Renewals.GeneralRenewalOpened e => new Contracts.GeneralRenewalOpened(e.RenewalId, e.PolicyId, e.ProductCode, e.PeriodEnd),
        Domain.Renewals.GeneralRenewalRenewed e => new Contracts.GeneralRenewalRenewed(e.RenewalId, e.PolicyId, e.ProductCode, e.PeriodEnd, e.CurrentPremium, e.RenewedPremium,
            e.Currency),
        Domain.Renewals.GeneralRenewalNotRenewed e => new Contracts.GeneralRenewalNotRenewed(e.RenewalId, e.PolicyId, e.ProductCode, e.PeriodEnd),
        Domain.Retention.RetentionCaseOpened e => new Contracts.GeneralRetentionCaseOpened(e.CaseId, e.PolicyId, e.PolicyNumber, e.ProductCode, e.Trigger, e.RenewalId),
        Domain.Retention.RetentionOfferMade e => new Contracts.GeneralRetentionOfferMade(e.CaseId, e.OfferId, e.PolicyId, e.ProductCode, e.Kind),
        Domain.Retention.RetentionOfferAccepted e => new Contracts.GeneralRetentionOfferAccepted(e.CaseId, e.OfferId, e.PolicyId, e.ProductCode, e.Kind, e.Value, e.RenewalId,
            e.DecidedBy),
        Domain.Retention.RetentionCaseClosed e => new Contracts.GeneralRetentionCaseClosed(e.CaseId, e.PolicyId, e.ProductCode, e.Outcome),
        Domain.Renewals.GeneralNonRenewalRequested e => new BuildingBlocks.Contracts.Approvals.ApprovalRequested(Contracts.GeneralApprovals.NonRenewalBusinessRefType,
            e.RenewalId, Contracts.GeneralApprovals.NonRenewalApprovalType, e.RequestedBy, null, null, $"Do not renew policy {e.PolicyNumber} ({e.ReasonCode})", null,
            Contracts.GeneralPermissions.PolicyNonRenew),
        Domain.Renewals.GeneralPolicyNotRenewed e => new Communications.Contracts.NotificationRequested(Communications.Contracts.NotificationTypes.NonRenewalNotice,
            e.PolicyholderPartyId, "GENERAL_POLICY", e.PolicyId, new Dictionary<string, string>
            {
                ["policy_number"] = e.PolicyNumber, ["product_code"] = e.ProductCode, ["reason"] = e.CustomerWording,
                ["period_end"] = e.PeriodEnd.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                ["cover_ends_on"] = e.CoverEndsOn.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            }, e.ProductCode, e.OrgNodeCode),
        GeneralPolicyIntermediaryChanged e => new Contracts.GeneralPolicyIntermediaryChanged(e.PolicyId, e.PolicyNumber, e.ProductCode, e.PreviousIntermediaryCode,
            e.IntermediaryCode, e.EffectiveDate, e.PeriodEnd, e.BillingFrequency),
        GeneralCoverExtended e => new Contracts.GeneralCoverExtended(e.PolicyId, e.PolicyNumber, e.PreviousPeriodEnd, e.NewPeriodEnd, e.ExtraPremium, e.Currency),
        GeneralPolicyCancelled e => new Contracts.GeneralPolicyCancelled(e.PolicyId, e.PolicyNumber, e.ReasonCode, e.EffectiveDate, e.RefundAmount, e.Currency, e.ProductCode,
            e.ProductVersion),
        GeneralPolicyReinstated e => new Contracts.GeneralPolicyReinstated(e.PolicyId, e.PolicyNumber),
        // F006, Wave 4 addition 1: the grace notice and the non-payment cancellation notice, to the policyholder.
        GeneralPolicyEnteredGrace e => new Communications.Contracts.NotificationRequested(Communications.Contracts.NotificationTypes.GraceNotice, e.PolicyholderPartyId,
            "GENERAL_POLICY", e.PolicyId, new Dictionary<string, string>
            {
                ["policy_number"] = e.PolicyNumber, ["product_code"] = e.ProductCode, ["unpaid_since"] = e.ArrearsSince.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                ["grace_until"] = e.GraceUntil.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                ["cover_ends_on"] = e.CoverEndsOn.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            }, e.ProductCode, e.OrgNodeCode),
        GeneralPolicyCancelledForNonPayment e => new Communications.Contracts.NotificationRequested(Communications.Contracts.NotificationTypes.NonPaymentCancellation,
            e.PolicyholderPartyId, "GENERAL_POLICY", e.PolicyId, new Dictionary<string, string>
            {
                ["policy_number"] = e.PolicyNumber, ["product_code"] = e.ProductCode,
                ["cancelled_from"] = e.EffectiveDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                ["refund_amount"] = e.RefundAmount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), ["currency"] = e.Currency,
            }, e.ProductCode, e.OrgNodeCode),
        GeneralEndorsementApproved e => new Contracts.GeneralEndorsementApproved(e.EndorsementId, e.PolicyId, e.ApprovalId),
        EndorsementSubmitted e => new BuildingBlocks.Contracts.Approvals.ApprovalRequested(Contracts.GeneralApprovals.EndorsementBusinessRefType, e.EndorsementId,
            Contracts.GeneralApprovals.EndorsementApprovalType, e.RequestedBy, Math.Abs(e.PremiumDelta), e.Currency,
            e.ExceededLimit is null ? $"Endorsement {e.EndorsementNumber}" : $"Endorsement {e.EndorsementNumber}; exceeds aggregation limit {e.ExceededLimit} (F062)"),
        GeneralClaimRegistered e => new Contracts.GeneralClaimRegistered(e.ClaimId, e.ClaimNumber, e.PolicyId, e.LossDate, e.PerilCode),
        AssessorAppointed e => new Contracts.AssessorAppointed(e.ClaimId, e.AssessorPartyId),
        SettlementRequested e => new BuildingBlocks.Contracts.Approvals.ApprovalRequested(Contracts.GeneralApprovals.SettlementBusinessRefType, e.ClaimId,
            Contracts.GeneralApprovals.SettlementApprovalType, e.RequestedBy, e.Amount, e.Currency, $"Settlement of claim {e.ClaimNumber}", e.SubjectHash),
        GeneralClaimApproved e => new Contracts.GeneralClaimApproved(e.ClaimId, e.ClaimNumber, e.PayeePartyId, e.BankAccountId, e.PayeeBankHash, e.Amount, e.Currency,
            e.ReserveType, e.ApprovalId, e.CheckerId, e.PolicyId, e.HoldReason, e.PremiumOffset),
        Domain.Claims.GeneralPremiumOffsetFromClaim e => new Contracts.GeneralPremiumOffsetFromClaim(e.PolicyId, e.ClaimId, e.ClaimNumber, e.Amount, e.Currency),
        GeneralPolicyArrearsCleared e => new Contracts.GeneralPolicyArrearsCleared(e.PolicyId, e.PolicyNumber),
        GeneralPolicyLapsedUnpaid e => new Contracts.GeneralPolicyLapsedUnpaid(e.PolicyId, e.PolicyNumber, e.CancelledFrom, e.ClaimDeclineReason),
        GeneralClaimSettled e => new Contracts.GeneralClaimSettled(e.ClaimId, e.ClaimNumber, e.Amount, e.Currency, e.PaidOn, e.PolicyId, e.PremiumOffset),
        GeneralClaimClosed e => new Contracts.GeneralClaimClosed(e.ClaimId, e.ClaimNumber),
        LossAssessed e => new Contracts.LossAssessed(e.AssessmentId, e.ClaimId, e.AssessedAmount, e.ExcessApplied),
        RecoveryRecorded e => new Contracts.RecoveryRecorded(e.RecoveryCaseId, e.ClaimId, e.ClaimNumber, e.RecoveryType, e.Amount, e.Currency, e.ReserveType),
        _ => null,
    };
}
