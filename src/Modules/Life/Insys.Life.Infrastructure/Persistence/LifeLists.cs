/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Life
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
using Insys.Life.Application;
using Insys.Life.Domain.Claims;
using Insys.Life.Domain.Policies;
using Microsoft.EntityFrameworkCore;

namespace Insys.Life.Infrastructure.Persistence;

/// <summary>Life's record lists (GAP-040): filters are exact matches, ordered by ID for cursor paging, over the records in the caller's scope (F103).</summary>
internal sealed class LifeLists(LifeDbContext db, IExecutionContext execution) : ILifeLists
{
    public async Task<IReadOnlyList<LifeProposalSummary>> Proposals(ListLifeProposals q, CancellationToken cancellationToken)
    {
        var rows = db.Proposals.AsNoTracking().InScope(execution.CurrentScope, p => p.ProductCode, p => p.OrgNodeCode);
        if (q.ApplicantPartyId is { } party) rows = rows.Where(p => p.ApplicantPartyId == party);
        if (q.ProposalNumber is { } number) rows = rows.Where(p => p.ProposalNumber == number);
        if (q.Status is { } status) rows = rows.Where(p => p.Status == status);
        if (q.AfterId is { } after) rows = rows.Where(p => p.Id.CompareTo(after) > 0);
        return await rows.OrderBy(p => p.Id).Take(q.Limit)
            .Select(p => new LifeProposalSummary(p.Id, p.ProposalNumber, p.ProductCode, p.ApplicantPartyId, p.Status, p.Currency, p.QuotedPremium, p.InceptionDate))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LifePolicySummary>> Policies(ListLifePolicies q, CancellationToken cancellationToken)
    {
        var rows = db.Policies.AsNoTracking().InScope(execution.CurrentScope, p => p.ProductCode, p => p.OrgNodeCode);
        if (q.PolicyholderPartyId is { } party) rows = rows.Where(p => p.PolicyholderPartyId == party);
        if (q.PolicyNumber is { } number) rows = rows.Where(p => p.PolicyNumber == number);
        if (q.Status is { } status) rows = rows.Where(p => p.Status == status);
        if (q.AfterId is { } after) rows = rows.Where(p => p.Id.CompareTo(after) > 0);
        return await rows.OrderBy(p => p.Id).Take(q.Limit)
            .Select(p => new LifePolicySummary(p.Id, p.PolicyNumber, p.ProductCode, p.PolicyholderPartyId, p.Status, p.InceptionDate, p.TermEndDate, p.Currency))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LifeClaimSummary>> Claims(ListLifeClaims q, CancellationToken cancellationToken)
    {
        var rows = db.Claims.AsNoTracking().InScope(execution.CurrentScope, c => c.ProductCode, c => c.OrgNodeCode);
        if (q.PolicyId is { } policy) rows = rows.Where(c => c.PolicyId == policy);
        if (q.ClaimantPartyId is { } party) rows = rows.Where(c => c.ClaimantPartyId == party);
        if (q.ClaimNumber is { } number) rows = rows.Where(c => c.ClaimNumber == number);
        if (q.Status is { } status) rows = rows.Where(c => c.Status == status);
        if (q.AfterId is { } after) rows = rows.Where(c => c.Id.CompareTo(after) > 0);
        return await rows.OrderBy(c => c.Id).Take(q.Limit)
            .Select(c => new LifeClaimSummary(c.Id, c.ClaimNumber, c.PolicyId, c.ClaimType, c.ClaimantPartyId, c.EventDate, c.Status, c.ApprovedAmount, c.Currency))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<GroupSchemeSummary>> Schemes(ListGroupSchemes q, CancellationToken cancellationToken)
    {
        var rows = db.Schemes.AsNoTracking().InScope(execution.CurrentScope, s => s.ProductCode, s => s.OrgNodeCode);
        if (q.EmployerPartyId is { } party) rows = rows.Where(s => s.EmployerPartyId == party);
        if (q.SchemeNumber is { } number) rows = rows.Where(s => s.SchemeNumber == number);
        if (q.Status is { } status) rows = rows.Where(s => s.Status == status);
        if (q.AfterId is { } after) rows = rows.Where(s => s.Id.CompareTo(after) > 0);
        return await rows.OrderBy(s => s.Id).Take(q.Limit)
            .Select(s => new GroupSchemeSummary(s.Id, s.SchemeNumber, s.EmployerPartyId, s.ProductCode, s.Status, s.StartDate, s.RenewalDate))
            .ToListAsync(cancellationToken);
    }
}

/// <summary>The portal's Life claims (external-user access §2), ordered by ID for cursor paging, within the caller's F103 scope.</summary>
internal sealed class LifePortalQueries(LifeDbContext db, IExecutionContext execution) : ILifePortalQueries
{
    public async Task<IReadOnlyList<LifeClaim>> Claims(ExternalSubject who, Guid? afterId, int limit, CancellationToken cancellationToken)
    {
        var rows = db.Claims.AsNoTracking().InScope(execution.CurrentScope, c => c.ProductCode, c => c.OrgNodeCode);
        if (who.Kind == ExternalSubject.Party && who.PartyId is { } party)
        {
            rows = rows.Where(c => c.ClaimantPartyId == party
                || db.Policies.Any(p => p.Id == c.PolicyId && p.PolicyholderPartyId == party)
                || db.Set<LifeBeneficiary>().Any(b => b.PolicyId == c.PolicyId && b.PartyId == party && b.ValidTo == null));
        }
        else if (who.Kind == ExternalSubject.Intermediary)
        {
            var codes = who.IntermediaryCodes.ToList();
            rows = rows.Where(c => db.Policies.Any(p => p.Id == c.PolicyId && p.IntermediaryCode != null && codes.Contains(p.IntermediaryCode)));
        }
        else
        {
            return [];
        }

        if (afterId is { } after)
        {
            rows = rows.Where(c => c.Id.CompareTo(after) > 0);
        }

        return await rows.OrderBy(c => c.Id).Take(limit).ToListAsync(cancellationToken);
    }
}
