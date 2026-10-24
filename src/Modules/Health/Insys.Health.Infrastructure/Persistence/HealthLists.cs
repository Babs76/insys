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
using Insys.Health.Application;
using Microsoft.EntityFrameworkCore;

namespace Insys.Health.Infrastructure.Persistence;

/// <summary>Health's record lists (GAP-040): filters are exact matches, ordered by ID for cursor paging.</summary>
internal sealed class HealthLists(HealthDbContext db) : IHealthLists
{
    public async Task<IReadOnlyList<MembershipSummary>> Memberships(ListMemberships q, CancellationToken cancellationToken)
    {
        var rows = db.Memberships.AsNoTracking();
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
        return await rows.OrderBy(r => r.Id).Take(q.Limit)
            .Select(r => new PaymentRunSummary(r.Id, r.RunCode, r.CutOff, r.ClaimCount, r.TotalAmount, r.Status, r.Currency))
            .ToListAsync(cancellationToken);
    }
}
