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
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.General.Application;
using Microsoft.EntityFrameworkCore;

namespace Insys.General.Infrastructure.Persistence;

/// <summary>
/// General's record lists (GAP-032), on the existing indexes and V0028's quote proposer index. Only records in the caller's
/// scope for the list's permission are returned, filtered in the query so paging stays correct (F103).
/// </summary>
internal sealed class GeneralLists(GeneralDbContext db, IExecutionContext execution) : IGeneralLists
{
    public async Task<IReadOnlyList<QuoteSummary>> Quotes(ListQuotes query, CancellationToken cancellationToken)
    {
        var quotes = db.Quotes.AsNoTracking().InScope(execution.CurrentScope, q => q.ProductCode, q => q.OrgNodeCode);
        if (query.ProposerPartyId is { } proposer)
        {
            quotes = quotes.Where(q => q.ProposerPartyId == proposer);
        }

        if (query.QuoteNumber is { } number)
        {
            quotes = quotes.Where(q => q.QuoteNumber == number);
        }

        if (query.AfterId is { } after)
        {
            quotes = quotes.Where(q => q.Id.CompareTo(after) > 0);
        }

        return await quotes.OrderBy(q => q.Id).Take(query.Limit)
            .Select(q => new QuoteSummary(q.Id, q.QuoteNumber, q.QuoteVersion, q.ProductCode, q.ProposerPartyId, q.Status, q.ValidUntil, q.PeriodStart, q.PeriodEnd,
                q.Premium, q.Currency))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PolicySummary>> Policies(ListPolicies query, CancellationToken cancellationToken)
    {
        var policies = db.Policies.AsNoTracking().InScope(execution.CurrentScope, p => p.ProductCode, p => p.OrgNodeCode);
        if (query.PolicyholderPartyId is { } holder)
        {
            policies = policies.Where(p => p.PolicyholderPartyId == holder);
        }

        if (query.PolicyNumber is { } number)
        {
            policies = policies.Where(p => p.PolicyNumber == number);
        }

        if (query.AfterId is { } after)
        {
            policies = policies.Where(p => p.Id.CompareTo(after) > 0);
        }

        return await policies.OrderBy(p => p.Id).Take(query.Limit)
            .Select(p => new PolicySummary(p.Id, p.PolicyNumber, p.ProductCode, p.PolicyholderPartyId, p.Status, p.PeriodStart, p.PeriodEnd, p.AnnualPremium, p.Currency))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ClaimSummary>> Claims(ListClaims query, CancellationToken cancellationToken)
    {
        var claims = db.Claims.AsNoTracking().InScope(execution.CurrentScope, c => c.ProductCode, c => c.OrgNodeCode);
        if (query.PolicyId is { } policy)
        {
            claims = claims.Where(c => c.PolicyId == policy);
        }

        if (query.ClaimNumber is { } number)
        {
            claims = claims.Where(c => c.ClaimNumber == number);
        }

        if (query.AfterId is { } after)
        {
            claims = claims.Where(c => c.Id.CompareTo(after) > 0);
        }

        return await claims.OrderBy(c => c.Id).Take(query.Limit)
            .Select(c => new ClaimSummary(c.Id, c.ClaimNumber, c.PolicyId, c.CoverageCode, c.LossDate, c.NotifiedAt, c.Status))
            .ToListAsync(cancellationToken);
    }
}

/// <summary>The portals' lists (external-user access §2): a member's policies and claims, a broker's quotes, policies and claims, in the caller's F103 scope too.</summary>
internal sealed class GeneralPortalQueries(GeneralDbContext db, IExecutionContext execution) : IGeneralPortalQueries
{
    public async Task<IReadOnlyList<Domain.Policies.GeneralPolicy>> Policies(ExternalSubject who, Guid? afterId, int limit, CancellationToken cancellationToken)
    {
        var policies = db.Policies.AsNoTracking().Include(p => p.Risks).ThenInclude(r => r.Coverages).AsSplitQuery()
            .InScope(execution.CurrentScope, p => p.ProductCode, p => p.OrgNodeCode);
        var codes = who.IntermediaryCodes.ToList();
        policies = who.Kind == ExternalSubject.Party
            ? policies.Where(p => p.PolicyholderPartyId == who.PartyId)
            : policies.Where(p => p.IntermediaryCode != null && codes.Contains(p.IntermediaryCode));
        if (afterId is { } after)
        {
            policies = policies.Where(p => p.Id.CompareTo(after) > 0);
        }

        return await policies.OrderBy(p => p.Id).Take(limit).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Domain.Underwriting.GeneralQuote>> Quotes(ExternalSubject who, Guid? afterId, int limit, CancellationToken cancellationToken)
    {
        var codes = who.IntermediaryCodes.ToList();
        var quotes = db.Quotes.AsNoTracking().Include(q => q.Risks).ThenInclude(r => r.Coverages).AsSplitQuery()
            .InScope(execution.CurrentScope, q => q.ProductCode, q => q.OrgNodeCode)
            .Where(q => q.IntermediaryCode != null && codes.Contains(q.IntermediaryCode));
        if (afterId is { } after)
        {
            quotes = quotes.Where(q => q.Id.CompareTo(after) > 0);
        }

        return await quotes.OrderBy(q => q.Id).Take(limit).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Domain.Claims.GeneralClaim>> Claims(ExternalSubject who, Guid? afterId, int limit, CancellationToken cancellationToken)
    {
        var claims = db.Claims.AsNoTracking().InScope(execution.CurrentScope, c => c.ProductCode, c => c.OrgNodeCode);
        var codes = who.IntermediaryCodes.ToList();
        claims = who.Kind == ExternalSubject.Party
            ? claims.Where(c => db.Policies.Any(p => p.Id == c.PolicyId && p.PolicyholderPartyId == who.PartyId))
            : claims.Where(c => db.Policies.Any(p => p.Id == c.PolicyId && p.IntermediaryCode != null && codes.Contains(p.IntermediaryCode)));
        if (afterId is { } after)
        {
            claims = claims.Where(c => c.Id.CompareTo(after) > 0);
        }

        return await claims.OrderBy(c => c.Id).Take(limit).ToListAsync(cancellationToken);
    }

    public async Task<bool> Services(ExternalSubject broker, Guid partyId, CancellationToken cancellationToken)
    {
        var codes = broker.IntermediaryCodes.ToList();
        return await db.Quotes.AnyAsync(q => q.ProposerPartyId == partyId && q.IntermediaryCode != null && codes.Contains(q.IntermediaryCode), cancellationToken)
            || await db.Policies.AnyAsync(p => p.PolicyholderPartyId == partyId && p.IntermediaryCode != null && codes.Contains(p.IntermediaryCode), cancellationToken);
    }
}
