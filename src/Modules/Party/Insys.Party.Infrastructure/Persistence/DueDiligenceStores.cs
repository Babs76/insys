/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Party
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
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Party.Application;
using Insys.Party.Domain.Parties;
using Insys.Party.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Text;

namespace Insys.Party.Infrastructure.Persistence;

internal sealed class DueDiligenceStore(PartyDbContext db) : IDueDiligenceStore
{
    public Task<DueDiligence?> ForParty(Guid partyId, CancellationToken cancellationToken) =>
        db.DueDiligences.Include(d => d.Hits).SingleOrDefaultAsync(d => d.PartyId == partyId, cancellationToken);

    public async Task<IReadOnlyList<BeneficialOwner>> Owners(Guid partyId, CancellationToken cancellationToken) =>
        await db.BeneficialOwners.Where(o => o.PartyId == partyId).OrderBy(o => o.RecordedAt).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<DueDiligence>> List(DueDiligenceFilter filter, Guid? beforeId, int limit, CancellationToken cancellationToken)
    {
        var query = db.DueDiligences.AsNoTracking().Include(d => d.Hits).AsQueryable();
        if (filter.Status is { } status)
        {
            query = query.Where(d => d.Status == status);
        }

        if (filter.RiskClass is { } riskClass)
        {
            query = query.Where(d => d.RiskClass == riskClass);
        }

        if (filter.ReviewDueBy is { } by)
        {
            query = query.Where(d => d.NextReviewOn != null && d.NextReviewOn <= by);
        }

        if (filter.OpenHits is { } open)
        {
            query = open ? query.Where(d => d.Hits.Any(h => h.Review == null)) : query.Where(d => d.Hits.All(h => h.Review != null));
        }

        if (beforeId is { } before)
        {
            query = query.Where(d => d.Id.CompareTo(before) < 0);
        }

        return await query.OrderByDescending(d => d.Id).Take(limit).ToListAsync(cancellationToken);
    }

    public async Task<(IReadOnlyList<Guid> Expiring, IReadOnlyList<Guid> Rescreen)> Due(DateOnly today, string? listVersion, DateTimeOffset screenedBefore,
        CancellationToken cancellationToken)
    {
        var expiring = await db.DueDiligences.AsNoTracking().Where(d => d.Status == DueDiligenceCodes.Cleared && d.NextReviewOn != null && d.NextReviewOn <= today)
            .OrderBy(d => d.NextReviewOn).Select(d => d.PartyId).Take(500).ToListAsync(cancellationToken);
        var rescreen = await db.DueDiligences.AsNoTracking()
            .Where(d => !d.Blocked && (d.LastScreenedAt == null || d.LastScreenedAt < screenedBefore || (listVersion != null && d.ScreenedListVersion != listVersion)))
            .OrderBy(d => d.LastScreenedAt).Select(d => d.PartyId).Take(500).ToListAsync(cancellationToken);
        return (expiring, rescreen);
    }

    public void Add(DueDiligence dueDiligence) => db.Add(dueDiligence);

    public void Add(BeneficialOwner owner) => db.Add(owner);
}

/// <summary>The due diligence configuration in force (scope GT): a tenant's rows replace the platform's for the same key.</summary>
internal sealed class DueDiligenceConfiguration(PartyDbContext db) : IDueDiligenceConfiguration
{
    public async Task<IReadOnlyList<string>> Requirements(string partyType, string level, IReadOnlyCollection<string> domains, DateOnly day,
        CancellationToken cancellationToken)
    {
        var rows = await InForce<DueDiligenceRequirements>(day).Where(r => r.PartyType == partyType && r.DdLevel == level && domains.Contains(r.Domain))
            .ToListAsync(cancellationToken);
        return [.. rows.GroupBy(r => r.Domain).SelectMany(Own).Select(r => r.CheckType).Distinct()];
    }

    public async Task<IReadOnlyList<RiskFactor>> RiskFactors(DateOnly day, CancellationToken cancellationToken) =>
        [.. Own(await InForce<CustomerRiskFactors>(day).ToListAsync(cancellationToken)).Select(f => new RiskFactor(f.Factor, f.MatchValues, f.Weight))];

    public async Task<IReadOnlyList<RiskClassBand>> RiskClasses(DateOnly day, CancellationToken cancellationToken) =>
        [.. Own(await InForce<CustomerRiskClasses>(day).ToListAsync(cancellationToken)).Select(c => new RiskClassBand(c.RiskClass, c.MinScore, c.MaxScore, c.DdLevel, c.ReviewInterval))];

    public async Task<bool> AdverseMediaEnabled(DateOnly day, CancellationToken cancellationToken) =>
        (await Settings(day, cancellationToken))?.AdverseMediaEnabled ?? false;

    public async Task<bool?> GatesEnabled(DateOnly day, CancellationToken cancellationToken) => (await Settings(day, cancellationToken))?.GatesEnabled;

    public async Task<(string Requirement, string? ScreeningMaxAge)?> Gate(string domain, string step, DateOnly day, CancellationToken cancellationToken)
    {
        var gate = (await InForce<DueDiligenceGates>(day).Where(g => g.Domain == domain && g.Step == step).ToListAsync(cancellationToken))
            .OrderByDescending(g => g.TenantId is not null).ThenByDescending(g => g.Version).FirstOrDefault();
        return gate is null ? null : (gate.Requirement, gate.ScreeningMaxAge);
    }

    public DateOnly After(DateOnly day, string interval) =>
        PeriodPattern.NormalizingIso.Parse(interval) is { Success: true } period ? LocalDate.FromDateOnly(day).Plus(period.Value).ToDateOnly() : day.AddYears(3);

    public DateTimeOffset Before(DateTimeOffset at, string interval) =>
        PeriodPattern.NormalizingIso.Parse(interval) is { Success: true } period
            ? LocalDateTime.FromDateTime(at.UtcDateTime).Minus(period.Value).InUtc().ToDateTimeOffset()
            : at.AddDays(-30);

    /// <summary>The tenant's settings in force: the one in force from the latest day, should two codes overlap.</summary>
    private Task<DueDiligenceSettings?> Settings(DateOnly day, CancellationToken cancellationToken) =>
        InForce<DueDiligenceSettings>(day).OrderByDescending(s => s.EffectiveFrom).ThenByDescending(s => s.Version).FirstOrDefaultAsync(cancellationToken);

    /// <summary>A tenant that configured its own rows uses them; otherwise the platform's.</summary>
    private static IEnumerable<T> Own<T>(IEnumerable<T> rows) where T : StaticRecord
    {
        var list = rows.ToList();
        return list.Any(r => r.TenantId is not null) ? list.Where(r => r.TenantId is not null) : list;
    }

    private IQueryable<T> InForce<T>(DateOnly day) where T : StaticRecord =>
        db.Set<T>().AsNoTracking().InForce(PartyStaticData.Descriptors.Single(d => d.EntityType == typeof(T)).Lifecycle, day);
}
