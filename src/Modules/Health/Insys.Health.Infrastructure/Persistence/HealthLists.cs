/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Health
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
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.Health.Application;
using Microsoft.EntityFrameworkCore;

namespace Insys.Health.Infrastructure.Persistence;

/// <summary>
/// Health's record lists (GAP-040): filters are exact matches, ordered by ID for cursor paging, over the records in the
/// caller's scope (F103): memberships by their node, and preauthorisations, claims and cases by their membership's.
/// </summary>
internal sealed class HealthLists(HealthDbContext db, IExecutionContext execution) : IHealthLists
{
    private IQueryable<Guid> MembershipsInScope => db.Memberships.AsNoTracking().InScope(execution.CurrentScope, m => m.ProductCode, m => m.OrgNodeCode).Select(m => m.Id);

    public async Task<IReadOnlyList<MembershipSummary>> Memberships(ListMemberships q, CancellationToken cancellationToken)
    {
        var rows = db.Memberships.AsNoTracking().InScope(execution.CurrentScope, m => m.ProductCode, m => m.OrgNodeCode);
        if (q.PrincipalPartyId is { } party) rows = rows.Where(m => m.PrincipalPartyId == party);
        if (q.MemberNumber is { } number) rows = rows.Where(m => m.MemberNumber == number);
        if (q.SchemeId is { } scheme) rows = rows.Where(m => m.SchemeId == scheme);
        if (q.Status is { } status) rows = rows.Where(m => m.Status == status);
        if (q.AfterId is { } after) rows = rows.Where(m => m.Id.CompareTo(after) > 0);
        return await rows.OrderBy(m => m.Id).Take(q.Limit)
            .Select(m => new MembershipSummary(m.Id, m.MemberNumber, m.PrincipalPartyId, m.SchemeId, m.PlanCode, m.OptionCode, m.Status, m.StartDate, m.EndDate))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PreauthorisationSummary>> Preauthorisations(ListPreauthorisations q, CancellationToken cancellationToken)
    {
        var rows = db.Preauthorisations.AsNoTracking();
        if (!execution.CurrentScope.IsUnrestricted) rows = rows.Where(p => MembershipsInScope.Contains(p.MembershipId));
        if (q.MembershipId is { } membership) rows = rows.Where(p => p.MembershipId == membership);
        if (q.PatientPartyId is { } party) rows = rows.Where(p => p.PatientPartyId == party);
        if (q.AuthNumber is { } number) rows = rows.Where(p => p.AuthNumber == number);
        if (q.Status is { } status) rows = rows.Where(p => p.Status == status);
        if (q.AfterId is { } after) rows = rows.Where(p => p.Id.CompareTo(after) > 0);
        return await rows.OrderBy(p => p.Id).Take(q.Limit)
            .Select(p => new PreauthorisationSummary(p.Id, p.AuthNumber, p.MembershipId, p.PatientPartyId, p.ProviderId, p.Status, p.RequestedAmount, p.AuthorisedAmount,
                p.ValidFrom, p.ValidTo))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MedicalClaimSummary>> Claims(ListMedicalClaims q, CancellationToken cancellationToken)
    {
        var rows = db.Claims.AsNoTracking();
        if (!execution.CurrentScope.IsUnrestricted) rows = rows.Where(c => MembershipsInScope.Contains(c.MembershipId));
        if (q.MembershipId is { } membership) rows = rows.Where(c => c.MembershipId == membership);
        if (q.PatientPartyId is { } party) rows = rows.Where(c => c.PatientPartyId == party);
        if (q.ClaimNumber is { } number) rows = rows.Where(c => c.ClaimNumber == number);
        if (q.Status is { } status) rows = rows.Where(c => c.Status == status);
        if (q.AfterId is { } after) rows = rows.Where(c => c.Id.CompareTo(after) > 0);
        return await rows.OrderBy(c => c.Id).Take(q.Limit)
            .Select(c => new MedicalClaimSummary(c.Id, c.ClaimNumber, c.MembershipId, c.PatientPartyId, c.ProviderId, c.ServiceDate, c.Status, c.TotalClaimed, c.TotalPaid,
                c.Currency))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<UtilisationCaseSummary>> UtilisationCases(ListUtilisationCases q, CancellationToken cancellationToken)
    {
        var rows = db.UtilisationCases.AsNoTracking();
        if (!execution.CurrentScope.IsUnrestricted) rows = rows.Where(c => MembershipsInScope.Contains(c.MembershipId));
        if (q.MembershipId is { } membership) rows = rows.Where(c => c.MembershipId == membership);
        if (q.ClaimId is { } claim) rows = rows.Where(c => c.ClaimId == claim);
        if (q.Status is { } status) rows = rows.Where(c => c.Status == status);
        if (q.AfterId is { } after) rows = rows.Where(c => c.Id.CompareTo(after) > 0);
        return await rows.OrderBy(c => c.Id).Take(q.Limit)
            .Select(c => new UtilisationCaseSummary(c.Id, c.MembershipId, c.RuleCode, c.Status, c.ClaimId, c.OpenedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<FraudCaseSummary>> FraudCases(ListFraudCases q, CancellationToken cancellationToken)
    {
        var rows = db.FraudCases.AsNoTracking();
        if (!execution.CurrentScope.IsUnrestricted)
        {
            var claimsInScope = db.Claims.AsNoTracking().Where(c => MembershipsInScope.Contains(c.MembershipId)).Select(c => c.ClaimNumber);
            rows = rows.Where(c => c.SubjectType != "CLAIM" || claimsInScope.Contains(c.SubjectRef));
        }

        if (q.ClaimNumber is { } number) rows = rows.Where(c => c.SubjectType == "CLAIM" && c.SubjectRef == number);
        if (q.Status is { } status) rows = rows.Where(c => c.Status == status);
        if (q.AfterId is { } after) rows = rows.Where(c => c.Id.CompareTo(after) > 0);
        return (await rows.OrderBy(c => c.Id).Take(q.Limit).ToListAsync(cancellationToken))
            .Select(c => new FraudCaseSummary(c.Id, c.SubjectType, c.SubjectRef, c.IndicatorCodes, c.Status, c.OpenedAt)).ToList();
    }

    public async Task<IReadOnlyList<PaymentRunSummary>> Runs(ListPaymentRuns q, CancellationToken cancellationToken)
    {
        var rows = db.Runs.AsNoTracking();
        if (q.RunCode is { } code) rows = rows.Where(r => r.RunCode == code);
        if (q.Status is { } status) rows = rows.Where(r => r.Status == status);
        if (q.AfterId is { } after) rows = rows.Where(r => r.Id.CompareTo(after) > 0);
        if (execution.CurrentScope.IsUnrestricted)
        {
            return await rows.OrderBy(r => r.Id).Take(q.Limit)
                .Select(r => new PaymentRunSummary(r.Id, r.RunCode, r.CutOff, r.ClaimCount, r.TotalAmount, r.Status, r.Currency))
                .ToListAsync(cancellationToken);
        }

        // F103: a run partly in scope counts only its remittances in scope; one with none in scope is left out.
        var summaries = new List<PaymentRunSummary>();
        foreach (var run in await rows.Include(r => r.Remittances).OrderBy(r => r.Id).Take(q.Limit).ToListAsync(cancellationToken))
        {
            var claims = await db.Claims.AsNoTracking().Where(c => c.PaymentRunId == run.Id).ToListAsync(cancellationToken);
            var ids = claims.Select(c => c.MembershipId).Distinct().ToList();
            var memberships = await db.Memberships.AsNoTracking().Where(m => ids.Contains(m.Id))
                .ToDictionaryAsync(m => m.Id, m => new MembershipScope(m.MemberNumber, m.ProductCode, m.OrgNodeCode), cancellationToken);
            var (visible, partly) = RunVisibility.Of(run.Remittances, claims, memberships, execution.CurrentScope);
            if (visible.Count > 0 || run.Remittances.Count == 0)
            {
                summaries.Add(new PaymentRunSummary(run.Id, run.RunCode, run.CutOff, partly ? visible.Sum(r => r.ClaimCount) : run.ClaimCount,
                    partly ? visible.Sum(r => r.Amount) : run.TotalAmount, run.Status, run.Currency, partly));
            }
        }

        return summaries;
    }
}

/// <summary>A provider's own claims and remittances, for the provider portal (external-user access §2).</summary>
internal sealed class HealthPortalQueries(HealthDbContext db) : IHealthPortalQueries
{
    public async Task<IReadOnlyList<Domain.Care.MedicalClaim>> ProviderClaims(string providerId, Guid? afterId, int limit, CancellationToken cancellationToken)
    {
        var claims = db.Claims.AsNoTracking().Include(c => c.Lines).Where(c => c.ProviderId == providerId);
        if (afterId is { } after)
        {
            claims = claims.Where(c => c.Id.CompareTo(after) > 0);
        }

        return await claims.OrderBy(c => c.Id).Take(limit).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PortalRemittanceView>> ProviderRemittances(string providerId, Guid? afterId, int limit, CancellationToken cancellationToken)
    {
        var runs = db.Runs.AsNoTracking().SelectMany(r => r.Remittances.Where(x => x.ProviderId == providerId),
            (r, x) => new { x.Id, r.RunCode, x.Amount, x.ClaimCount, r.Currency, x.PaymentId, x.PaidOn });
        if (afterId is { } after)
        {
            runs = runs.Where(x => x.Id.CompareTo(after) > 0);
        }

        return (await runs.OrderBy(x => x.Id).Take(limit).ToListAsync(cancellationToken))
            .Select(x => new PortalRemittanceView(x.Id, x.RunCode, x.Amount, x.ClaimCount, x.Currency, x.PaymentId, x.PaidOn)).ToList();
    }
}
