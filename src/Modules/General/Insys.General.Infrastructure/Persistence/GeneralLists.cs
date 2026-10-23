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
using Insys.General.Application;
using Microsoft.EntityFrameworkCore;

namespace Insys.General.Infrastructure.Persistence;

/// <summary>General's record lists (GAP-032), on the existing indexes and V0028's quote proposer index.</summary>
internal sealed class GeneralLists(GeneralDbContext db) : IGeneralLists
{
    public async Task<IReadOnlyList<QuoteSummary>> Quotes(ListQuotes query, CancellationToken cancellationToken)
    {
        var quotes = db.Quotes.AsNoTracking();
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
        var policies = db.Policies.AsNoTracking();
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
        var claims = db.Claims.AsNoTracking();
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
