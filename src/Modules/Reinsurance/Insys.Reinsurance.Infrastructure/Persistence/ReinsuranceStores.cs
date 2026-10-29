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
using System.Text.Json;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Reinsurance.Application;
using Insys.Reinsurance.Domain;
using Insys.Reinsurance.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;

namespace Insys.Reinsurance.Infrastructure.Persistence;

internal sealed class ReinsuranceRepository(ReinsuranceDbContext db, IBusinessDate businessDate) : IReinsuranceRepository
{
    public async Task<IReadOnlyList<Cession>> CurrentCessions(string sourceType, Guid policyRef, CancellationToken cancellationToken)
    {
        var all = await CessionsFor(sourceType, policyRef, cancellationToken);
        var superseded = all.Where(c => c.SupersedesId is not null).Select(c => c.SupersedesId!.Value).ToHashSet();
        return all.Where(c => !superseded.Contains(c.Id)).ToList();
    }

    public async Task<IReadOnlyList<Cession>> CessionsFor(string sourceType, Guid policyRef, CancellationToken cancellationToken)
    {
        var stored = await db.Cessions.AsNoTracking().Where(c => c.SourceType == sourceType && c.SourceId == policyRef).ToListAsync(cancellationToken);

        // Cessions added in this unit of work count too.
        var added = db.ChangeTracker.Entries<Cession>().Where(e => e.State == EntityState.Added && e.Entity.SourceType == sourceType && e.Entity.SourceId == policyRef)
            .Select(e => e.Entity);
        return stored.Concat(added).OrderBy(c => c.CalculatedAt).ThenBy(c => c.Id).ToList();
    }

    public async Task<IReadOnlyList<Cession>> CessionsCalculated(string treatyCode, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        // The tenant's business days, not UTC days: a cession at 23:30 UTC on the 31st is the next month's in Johannesburg.
        var (start, end) = (StartOf(from), StartOf(to.AddDays(1)));
        return await db.Cessions.AsNoTracking().Where(c => c.TreatyCode == treatyCode && c.CalculatedAt >= start && c.CalculatedAt < end)
            .OrderBy(c => c.CalculatedAt).ThenBy(c => c.Id).ToListAsync(cancellationToken);
    }

    /// <summary>The instant a business day starts in the tenant's time zone (ADR-025).</summary>
    private DateTimeOffset StartOf(DateOnly day) =>
        new(TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue), businessDate.Zone), TimeSpan.Zero);

    public Task<ReinsuranceRecovery?> Recovery(Guid id, CancellationToken cancellationToken) => db.Recoveries.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);

    public async Task<ReinsuranceRecovery?> Recovery(string claimNumber, string treatyCode, CancellationToken cancellationToken) =>
        db.ChangeTracker.Entries<ReinsuranceRecovery>().Select(e => e.Entity).FirstOrDefault(r => r.ClaimNumber == claimNumber && r.TreatyCode == treatyCode)
        ?? await db.Recoveries.SingleOrDefaultAsync(r => r.ClaimNumber == claimNumber && r.TreatyCode == treatyCode, cancellationToken);

    public async Task<IReadOnlyList<Cession>> Cessions(CessionSearch search, Guid? afterId, int limit, CancellationToken cancellationToken)
    {
        var query = db.Cessions.AsNoTracking();
        if (search.SourceDomain is { } domain && search.PolicyRef is { } policy)
        {
            var sourceType = Cession.SourceTypeOf(domain);
            query = query.Where(c => c.SourceType == sourceType && c.SourceId == policy);
        }

        if (search.TreatyCode is { } treaty)
        {
            query = query.Where(c => c.TreatyCode == treaty);
        }

        if (search.PeriodCode is { } period && DateOnly.TryParseExact(period + "-01", "yyyy-MM-dd", out var first))
        {
            var (start, end) = (StartOf(first), StartOf(first.AddMonths(1)));
            query = query.Where(c => c.CalculatedAt >= start && c.CalculatedAt < end);
        }

        if (afterId is { } after)
        {
            query = query.Where(c => c.Id.CompareTo(after) > 0);
        }

        return await query.OrderBy(c => c.Id).Take(limit).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Bordereau>> Bordereaux(string? treatyCode, string? periodCode, BordereauStatus? status, Guid? afterId, int limit,
        CancellationToken cancellationToken)
    {
        var query = db.Bordereaux.AsNoTracking();
        if (treatyCode is not null)
        {
            query = query.Where(b => b.TreatyCode == treatyCode);
        }

        if (periodCode is not null)
        {
            query = query.Where(b => b.PeriodCode == periodCode);
        }

        if (status is { } s)
        {
            query = query.Where(b => b.Status == s);
        }

        if (afterId is { } after)
        {
            query = query.Where(b => b.Id.CompareTo(after) > 0);
        }

        return await query.OrderBy(b => b.Id).Take(limit).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ReinsuranceRecovery>> Recoveries(string? claimNumber, RecoveryStatus? status, Guid? afterId, int limit,
        CancellationToken cancellationToken)
    {
        var query = db.Recoveries.AsNoTracking();
        if (claimNumber is not null)
        {
            query = query.Where(r => r.ClaimNumber == claimNumber);
        }

        if (status is { } s)
        {
            query = query.Where(r => r.Status == s);
        }

        if (afterId is { } after)
        {
            query = query.Where(r => r.Id.CompareTo(after) > 0);
        }

        return await query.OrderBy(r => r.Id).Take(limit).ToListAsync(cancellationToken);
    }

    public Task<Bordereau?> Bordereau(Guid id, CancellationToken cancellationToken) => db.Bordereaux.SingleOrDefaultAsync(b => b.Id == id, cancellationToken);

    public Task<Bordereau?> Bordereau(string treatyCode, string periodCode, CancellationToken cancellationToken) =>
        db.Bordereaux.SingleOrDefaultAsync(b => b.TreatyCode == treatyCode && b.PeriodCode == periodCode, cancellationToken);

    public void Add(Cession cession) => db.Cessions.Add(cession);

    public void Add(ReinsuranceRecovery recovery) => db.Recoveries.Add(recovery);

    public void Add(Bordereau bordereau) => db.Bordereaux.Add(bordereau);
}

internal sealed class ReinsuranceCodes(ReinsuranceDbContext db) : IReinsuranceCodes
{
    public async Task<IReadOnlyList<(CessionRule Rule, TreatyTerms Treaty)>> ArrangementsFor(string productCode, DateOnly day, CancellationToken cancellationToken)
    {
        var rules = await InForce<CessionRules>(day).Where(r => r.Products.Contains(productCode)).OrderBy(r => r.RuleCode).ToListAsync(cancellationToken);
        var arrangements = new List<(CessionRule, TreatyTerms)>();
        foreach (var rule in rules)
        {
            if (await Treaty(rule.Treaty, day, cancellationToken) is { } treaty)
            {
                arrangements.Add((new CessionRule(rule.RuleCode, rule.Version, rule.Treaty, rule.Products, rule.AllocationBasis), treaty));
            }
        }

        return arrangements;
    }

    public async Task<TreatyTerms?> Treaty(string treatyCode, DateOnly day, CancellationToken cancellationToken) =>
        await InForce<TreatyDefinitions>(day).Where(t => t.TreatyCode == treatyCode).OrderByDescending(t => t.Version).FirstOrDefaultAsync(cancellationToken) is { } t
            ? new TreatyTerms(t.TreatyCode, t.Version, t.Type, t.Retention, t.Capacity, t.PeriodFrom, t.PeriodTo, Lead(t.Participants))
            : null;

    public async Task<FacultativeArrangement?> Facultative(string riskReference, DateOnly day, CancellationToken cancellationToken) =>
        await InForce<FacultativeArrangements>(day).Where(f => f.RiskReference == riskReference).OrderBy(f => f.ArrangementCode)
            .Select(f => new FacultativeArrangement(f.ArrangementCode, f.Version, f.RiskReference, f.Reinsurer, f.Share)).FirstOrDefaultAsync(cancellationToken);

    public async Task<BordereauLayout?> Layout(string layoutCode, DateOnly day, CancellationToken cancellationToken) =>
        await InForce<BordereauxLayouts>(day).Where(l => l.LayoutCode == layoutCode).OrderByDescending(l => l.Version)
            .Select(l => new BordereauLayout(l.LayoutCode, l.Treaty, l.Fields, l.Frequency)).FirstOrDefaultAsync(cancellationToken);

    /// <summary>The first participant's reinsurer ([{"reinsurer": "RE-1", "share": 60}, ...]), as the treaty's partner for the adapter.</summary>
    private static string? Lead(JsonElement participants) =>
        participants.ValueKind == JsonValueKind.Array && participants.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } first
        && first.TryGetProperty("reinsurer", out var reinsurer) && reinsurer.ValueKind == JsonValueKind.String
            ? reinsurer.GetString()
            : null;

    private IQueryable<T> InForce<T>(DateOnly day) where T : StaticRecord =>
        db.Set<T>().AsNoTracking().InForce(ReinsuranceStaticData.Descriptors.Single(d => d.EntityType == typeof(T)).Lifecycle, day);
}

internal sealed class ReinsuranceEventMapper : IDomainEventMapper
{
    public IIntegrationEvent? Map(IDomainEvent domainEvent) => domainEvent switch
    {
        Domain.CessionCalculated e => new Contracts.CessionCalculated(e.CessionId, e.TreatyCode, e.SourceDomain, e.PolicyRef, e.CededSumInsured, e.CededPremium,
            e.PremiumChange, e.Currency, e.PeriodStart, e.PeriodEnd, e.RuleCode, e.RuleVersion, e.SupersedesId),
        Domain.RecoveryBilled e => new Contracts.RecoveryBilled(e.RecoveryId, e.ClaimNumber, e.TreatyCode, e.SourceDomain, e.Amount, e.Currency, e.BilledOn),
        Domain.RecoverySettled e => new Contracts.RecoverySettled(e.RecoveryId, e.ClaimNumber, e.TreatyCode, e.SourceDomain, e.Recoverable, e.Amount, e.Currency,
            e.SettledOn),
        Domain.BordereauSent e => new Contracts.BordereauSent(e.BordereauId, e.TreatyCode, e.PeriodCode, e.RowCount, e.PartnerRef),
        _ => null,
    };
}
