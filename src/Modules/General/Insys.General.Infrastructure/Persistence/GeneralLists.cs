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

    public async Task<IReadOnlyList<Domain.Underwriting.RiskObject>> RiskObjects(ListRiskObjects query, CancellationToken cancellationToken)
    {
        var risks = db.RiskObjects.AsNoTracking();
        if (query.ObjectType is { } type)
        {
            risks = risks.Where(r => r.ObjectType == type);
        }

        if (query.RiskZone is { } zone)
        {
            risks = risks.Where(r => r.RiskZone == zone);
        }

        if (query.OwnerPartyId is { } owner)
        {
            risks = risks.Where(r => r.OwnerPartyId == owner);
        }

        if (query.PolicyId is { } policyId)
        {
            var policies = db.Policies.AsNoTracking().InScope(execution.CurrentScope, p => p.ProductCode, p => p.OrgNodeCode).Where(p => p.Id == policyId);
            risks = risks.Where(r => policies.Any(p => p.Risks.Any(x => x.RiskObjectId == r.Id)));
        }

        if (query.AfterId is { } after)
        {
            risks = risks.Where(r => r.Id.CompareTo(after) > 0);
        }

        return await risks.OrderBy(r => r.Id).Take(query.Limit).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Domain.Underwriting.GeneralProposal>> Proposals(ListProposals query, CancellationToken cancellationToken)
    {
        var quotes = db.Quotes.AsNoTracking().InScope(execution.CurrentScope, q => q.ProductCode, q => q.OrgNodeCode);
        var proposals = db.Proposals.AsNoTracking().Where(p => quotes.Any(q => q.Id == p.QuoteId));
        if (query.QuoteId is { } quote)
        {
            proposals = proposals.Where(p => p.QuoteId == quote);
        }

        if (query.Status is { } status)
        {
            proposals = proposals.Where(p => p.Status == status);
        }

        if (query.AfterId is { } after)
        {
            proposals = proposals.Where(p => p.Id.CompareTo(after) > 0);
        }

        return await proposals.OrderBy(p => p.Id).Take(query.Limit).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Domain.Policies.GeneralEndorsement>> Endorsements(Guid policyId, Guid? afterId, int limit, CancellationToken cancellationToken)
    {
        var endorsements = db.Endorsements.AsNoTracking().Where(e => e.PolicyId == policyId);
        if (afterId is { } after)
        {
            endorsements = endorsements.Where(e => e.Id.CompareTo(after) > 0);
        }

        return await endorsements.OrderBy(e => e.Id).Take(limit).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Domain.Claims.LossAssessment>> Assessments(ListAssessments query, CancellationToken cancellationToken)
    {
        var claims = db.Claims.AsNoTracking().InScope(execution.CurrentScope, c => c.ProductCode, c => c.OrgNodeCode);
        var assessments = db.Assessments.AsNoTracking().Include(a => a.Estimates).Where(a => claims.Any(c => c.Id == a.ClaimId));
        if (query.ClaimId is { } claim)
        {
            assessments = assessments.Where(a => a.ClaimId == claim);
        }

        if (query.Status is { } status)
        {
            assessments = assessments.Where(a => a.Status == status);
        }

        if (query.AssessorPartyId is { } assessor)
        {
            assessments = assessments.Where(a => a.AssessorPartyId == assessor);
        }

        if (query.AfterId is { } after)
        {
            assessments = assessments.Where(a => a.Id.CompareTo(after) > 0);
        }

        return await assessments.OrderBy(a => a.Id).Take(query.Limit).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Domain.Claims.RecoveryCase>> Recoveries(Guid claimId, Guid? afterId, int limit, CancellationToken cancellationToken)
    {
        var recoveries = db.Recoveries.AsNoTracking().Include(r => r.Salvage).Where(r => r.ClaimId == claimId);
        if (afterId is { } after)
        {
            recoveries = recoveries.Where(r => r.Id.CompareTo(after) > 0);
        }

        return await recoveries.OrderBy(r => r.Id).Take(limit).ToListAsync(cancellationToken);
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

    public async Task<bool?> CurrentlyServices(ExternalSubject broker, Guid partyId, CancellationToken cancellationToken)
    {
        var codes = broker.IntermediaryCodes.ToList();
        var live = db.Policies.AsNoTracking()
            .Where(p => p.PolicyholderPartyId == partyId && (p.Status == Domain.Policies.PolicyStatus.Issued || p.Status == Domain.Policies.PolicyStatus.InForce));
        if (await live.AnyAsync(cancellationToken))
        {
            return await live.AnyAsync(p => p.IntermediaryCode != null && codes.Contains(p.IntermediaryCode), cancellationToken);
        }

        // UUIDv7 IDs sort by creation, so the highest is the latest quote.
        var latest = await db.Quotes.AsNoTracking().Where(q => q.ProposerPartyId == partyId).OrderByDescending(q => q.Id).Select(q => new { q.IntermediaryCode })
            .FirstOrDefaultAsync(cancellationToken);
        return latest is null ? null : latest.IntermediaryCode is { } code && codes.Contains(code);
    }

    public async Task<IReadOnlyList<Domain.Underwriting.RiskObject>> RiskObjects(Guid ownerPartyId, Guid? afterId, int limit, CancellationToken cancellationToken)
    {
        var risks = db.RiskObjects.AsNoTracking().Where(r => r.OwnerPartyId == ownerPartyId);
        if (afterId is { } after)
        {
            risks = risks.Where(r => r.Id.CompareTo(after) > 0);
        }

        return await risks.OrderBy(r => r.Id).Take(limit).ToListAsync(cancellationToken);
    }

    public async Task<bool> Services(ExternalSubject broker, Guid partyId, CancellationToken cancellationToken)
    {
        var codes = broker.IntermediaryCodes.ToList();
        return await db.Quotes.AnyAsync(q => q.ProposerPartyId == partyId && q.IntermediaryCode != null && codes.Contains(q.IntermediaryCode), cancellationToken)
            || await db.Policies.AnyAsync(p => p.PolicyholderPartyId == partyId && p.IntermediaryCode != null && codes.Contains(p.IntermediaryCode), cancellationToken);
    }
}
