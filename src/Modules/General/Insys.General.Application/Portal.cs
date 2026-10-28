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
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;
using Insys.General.Domain.Claims;
using Insys.General.Domain.Policies;
using Insys.General.Domain.Underwriting;
using Insys.Party.Contracts;

namespace Insys.General.Application;

// External-user access (docs/specs/addenda/external-user-access.md): what the member and broker portals see and do in
// General. Each record is checked against the caller's relationship (§2) as well as their F103 scope; out of it, a read
// is not found and an action refused. The views leave out internal fields (§4): no reserves, rationale, fraud,
// underwriting referrals, other parties or billing internals.

public sealed record PortalCoverageView(string CoverageCode, decimal SumInsured, decimal? Premium);

public sealed record PortalRiskView(Guid RiskObjectId, decimal SumInsured, IReadOnlyList<PortalCoverageView> Coverages);

public sealed record PortalPolicyView(Guid Id, string PolicyNumber, string ProductCode, PolicyStatus Status, DateOnly PeriodStart, DateOnly PeriodEnd, string Currency,
    decimal AnnualPremium, Guid PolicyholderPartyId, string? IntermediaryCode, IReadOnlyList<PortalRiskView> Risks)
{
    public static PortalPolicyView From(GeneralPolicy p) => new(p.Id, p.PolicyNumber, p.ProductCode, p.Status, p.PeriodStart, p.PeriodEnd, p.Currency, p.AnnualPremium,
        p.PolicyholderPartyId, p.IntermediaryCode,
        p.Risks.Where(r => r.DetachedOn is null).Select(r => new PortalRiskView(r.RiskObjectId, r.SumInsured,
            r.Coverages.Select(c => new PortalCoverageView(c.CoverageCode, c.SumInsured, c.Premium)).ToList())).ToList());
}

public sealed record PortalQuoteView(Guid Id, string QuoteNumber, int QuoteVersion, string ProductCode, Guid ProposerPartyId, string? IntermediaryCode, QuoteStatus Status,
    DateOnly PeriodStart, DateOnly PeriodEnd, decimal? Premium, string Currency, DateOnly ValidUntil, IReadOnlyList<PortalRiskView> Risks)
{
    public static PortalQuoteView From(GeneralQuote q) => new(q.Id, q.QuoteNumber, q.QuoteVersion, q.ProductCode, q.ProposerPartyId, q.IntermediaryCode, q.Status,
        q.PeriodStart, q.PeriodEnd, q.Premium, q.Currency, q.ValidUntil,
        q.Risks.Select(r => new PortalRiskView(r.RiskObjectId, r.SumInsured, r.Coverages.Select(c => new PortalCoverageView(c.CoverageCode, c.SumInsured, null)).ToList())).ToList());
}

/// <param name="SettlementAmount">What is to be paid, once a settlement is approved; nothing before.</param>
public sealed record PortalClaimView(Guid Id, string? ClaimNumber, Guid PolicyId, string CoverageCode, DateOnly LossDate, DateTimeOffset NotifiedAt, ClaimStatus Status,
    decimal? SettlementAmount, string? Currency)
{
    public static PortalClaimView From(GeneralClaim c) => new(c.Id, c.ClaimNumber, c.PolicyId, c.CoverageCode, c.LossDate, c.NotifiedAt, c.Status,
        c.Status is ClaimStatus.Approved or ClaimStatus.Settled or ClaimStatus.Closed ? c.SettlementAmount : null,
        c.Status is ClaimStatus.Approved or ClaimStatus.Settled or ClaimStatus.Closed ? c.SettlementCurrency : null);
}

/// <summary>Lists for the portals, in the query (so paging stays correct), within the caller's relationship and F103 scope.</summary>
public interface IGeneralPortalQueries
{
    Task<IReadOnlyList<GeneralPolicy>> Policies(ExternalSubject who, Guid? afterId, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<GeneralQuote>> Quotes(ExternalSubject who, Guid? afterId, int limit, CancellationToken cancellationToken);

    /// <summary>The claims on the caller's policies: the member's as policyholder, the broker's under its codes (BR-091).</summary>
    Task<IReadOnlyList<GeneralClaim>> Claims(ExternalSubject who, Guid? afterId, int limit, CancellationToken cancellationToken);

    /// <summary>Whether the party is a client of the broker: a quote or policy of theirs names one of the broker's codes.</summary>
    Task<bool> Services(ExternalSubject broker, Guid partyId, CancellationToken cancellationToken);
}

public sealed record ListPortalPolicies(Guid? AfterId, int Limit) : IQuery<IReadOnlyList<PortalPolicyView>>;

public sealed record GetPortalPolicy(Guid PolicyId) : IQuery<PortalPolicyView>;

public sealed record ListPortalQuotes(Guid? AfterId, int Limit) : IQuery<IReadOnlyList<PortalQuoteView>>;

public sealed record GetPortalQuote(Guid QuoteId) : IQuery<PortalQuoteView>;

public sealed record GetPortalClaim(Guid ClaimId) : IQuery<PortalClaimView>;

public sealed record ListPortalClaims(Guid? AfterId, int Limit) : IQuery<IReadOnlyList<PortalClaimView>>;

/// <summary>
/// Before a broker's quote (§3): the proposer is a client the broker services or one it registered. Gives the broker's own
/// intermediary code, which the quote is made under whatever the request says, and the proposer to quote for: a
/// registration staff linked to an existing party after review (§6) is quoted for as that party.
/// </summary>
public sealed record PortalQuoteAllowed(Guid ProposerPartyId) : IQuery<PortalQuoter>;

public sealed record PortalQuoter(string IntermediaryCode, Guid ProposerPartyId);

/// <summary>Before a member's claim notification (§3): the policy is theirs.</summary>
public sealed record PortalClaimAllowed(Guid PolicyId) : IQuery<bool>;

/// <summary>The relationship checks of the member and broker portals in General (§2).</summary>
internal static class Related
{
    public static bool Policy(ExternalSubject who, GeneralPolicy policy) =>
        who.Kind == ExternalSubject.Party ? policy.PolicyholderPartyId == who.PartyId : who.Brokers(policy.IntermediaryCode);

    public static bool Quote(ExternalSubject who, GeneralQuote quote) => who.Brokers(quote.IntermediaryCode);

    public static ExternalSubject Require(IExecutionContext execution) =>
        execution.External ?? throw new InvalidOperationException("A portal endpoint is used by external users only.");
}

internal sealed class GeneralPortalHandlers(IGeneralRepository general, IGeneralPortalQueries queries, IExecutionContext execution, IPartyDirectory parties,
    ITenantContext tenant) :
    IQueryHandler<ListPortalPolicies, IReadOnlyList<PortalPolicyView>>,
    IQueryHandler<GetPortalPolicy, PortalPolicyView>,
    IQueryHandler<ListPortalQuotes, IReadOnlyList<PortalQuoteView>>,
    IQueryHandler<GetPortalQuote, PortalQuoteView>,
    IQueryHandler<GetPortalClaim, PortalClaimView>,
    IQueryHandler<ListPortalClaims, IReadOnlyList<PortalClaimView>>,
    IQueryHandler<PortalQuoteAllowed, PortalQuoter>,
    IQueryHandler<PortalClaimAllowed, bool>
{
    private static readonly Error PolicyNotFound = Error.NotFound(ErrorCodes.NotFound, "The policy was not found.");

    public async Task<Result<IReadOnlyList<PortalPolicyView>>> Handle(ListPortalPolicies query, CancellationToken cancellationToken) =>
        Result.Success<IReadOnlyList<PortalPolicyView>>((await queries.Policies(Related.Require(execution), query.AfterId, query.Limit, cancellationToken))
            .Select(PortalPolicyView.From).ToList());

    public async Task<Result<PortalPolicyView>> Handle(GetPortalPolicy query, CancellationToken cancellationToken) =>
        await general.Policy(query.PolicyId, cancellationToken) is { } policy && Related.Policy(Related.Require(execution), policy)
            ? PortalPolicyView.From(policy)
            : PolicyNotFound;

    public async Task<Result<IReadOnlyList<PortalQuoteView>>> Handle(ListPortalQuotes query, CancellationToken cancellationToken) =>
        Result.Success<IReadOnlyList<PortalQuoteView>>((await queries.Quotes(Related.Require(execution), query.AfterId, query.Limit, cancellationToken))
            .Select(PortalQuoteView.From).ToList());

    public async Task<Result<PortalQuoteView>> Handle(GetPortalQuote query, CancellationToken cancellationToken) =>
        await general.Quote(query.QuoteId, cancellationToken) is { } quote && Related.Quote(Related.Require(execution), quote)
            ? PortalQuoteView.From(quote)
            : Error.NotFound(ErrorCodes.NotFound, "The quote was not found.");

    public async Task<Result<PortalClaimView>> Handle(GetPortalClaim query, CancellationToken cancellationToken) =>
        await general.Claim(query.ClaimId, cancellationToken) is { } claim && await general.Policy(claim.PolicyId, cancellationToken) is { } policy
        && Related.Policy(Related.Require(execution), policy)
            ? PortalClaimView.From(claim)
            : Error.NotFound(ErrorCodes.NotFound, "The claim was not found.");

    public async Task<Result<IReadOnlyList<PortalClaimView>>> Handle(ListPortalClaims query, CancellationToken cancellationToken) =>
        Result.Success<IReadOnlyList<PortalClaimView>>((await queries.Claims(Related.Require(execution), query.AfterId, query.Limit, cancellationToken))
            .Select(PortalClaimView.From).ToList());

    public async Task<Result<PortalQuoter>> Handle(PortalQuoteAllowed query, CancellationToken cancellationToken)
    {
        var broker = Related.Require(execution);
        var registeredBy = await parties.RegisteredBy(tenant.RequireTenantId(), query.ProposerPartyId, cancellationToken);
        var registered = registeredBy is not null && registeredBy.StartsWith($"{ExternalSubject.Intermediary}:", StringComparison.Ordinal)
            && broker.Brokers(registeredBy[(ExternalSubject.Intermediary.Length + 1)..]);
        if (broker.IntermediaryCode is not { } code || !(registered || await queries.Services(broker, query.ProposerPartyId, cancellationToken)))
        {
            return Error.Forbidden(ErrorCodes.OutOfScope, "You can quote only for a client you service or one you registered.");
        }

        var proposer = registered && await parties.MergedInto(tenant.RequireTenantId(), query.ProposerPartyId, cancellationToken) is { } existing
            ? existing
            : query.ProposerPartyId;
        return new PortalQuoter(code, proposer);
    }

    public async Task<Result<bool>> Handle(PortalClaimAllowed query, CancellationToken cancellationToken) =>
        await general.Policy(query.PolicyId, cancellationToken) is { } policy && Related.Policy(Related.Require(execution), policy) ? true : PolicyNotFound;
}
