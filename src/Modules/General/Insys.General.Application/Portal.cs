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

/// <param name="Premium">The annual premium Rating gave (BR-095): none while Draft, and none while Referred or once Declined, whatever was rated.</param>
/// <param name="RatingIssue">Why the quote could not be priced, while it stays Draft (BR-095); null otherwise.</param>
/// <param name="PremiumFrequency">The frequency the quote is priced in: the one the broker chose when the product allows it, otherwise <c>ANNUAL</c>.</param>
/// <param name="InstalmentPremium">Each instalment in <paramref name="PremiumFrequency"/>, as Billing will charge it; the premium itself when annual. Hidden with the premium.</param>
/// <param name="TotalPayable">When a loading applies: what the instalments come to over the year. Hidden with the premium.</param>
public sealed record PortalQuoteView(Guid Id, string QuoteNumber, int QuoteVersion, string ProductCode, Guid ProposerPartyId, string? IntermediaryCode, QuoteStatus Status,
    DateOnly PeriodStart, DateOnly PeriodEnd, decimal? Premium, string Currency, DateOnly ValidUntil, IReadOnlyList<PortalRiskView> Risks, RatingIssueView? RatingIssue = null,
    string PremiumFrequency = QuotePricing.Annual, decimal? InstalmentPremium = null, decimal? TotalPayable = null, bool PricedOnCreation = false)
{
    /// <summary>BR-097: PricedOnCreation tells "being priced" (true while Draft) from "waiting for the insurer's staff to price it" (false).</summary>
    public static PortalQuoteView From(GeneralQuote q)
    {
        var shown = q.Status is not (QuoteStatus.Referred or QuoteStatus.Declined);
        return new(q.Id, q.QuoteNumber, q.QuoteVersion, q.ProductCode, q.ProposerPartyId, q.IntermediaryCode, q.Status, q.PeriodStart, q.PeriodEnd,
            shown ? q.Premium : null, q.Currency, q.ValidUntil,
            q.Risks.Select(r => new PortalRiskView(r.RiskObjectId, r.SumInsured, r.Coverages.Select(c => new PortalCoverageView(c.CoverageCode, c.SumInsured, null)).ToList())).ToList(),
            RatingIssueView.Of(q), q.PremiumFrequency ?? QuotePricing.Annual, shown ? q.InstalmentPremium ?? q.Premium : null, shown ? q.TotalPayable : null,
            q.PricedOnCreation);
    }
}

/// <summary>A broker's view of an insured item (BR-092): what it registered or may quote on, without the schema version or row version.</summary>
public sealed record PortalRiskObjectView(Guid Id, [property: CodeList(CodeLists.RiskObjectTypes, "object_type")] string ObjectType, System.Text.Json.JsonElement Attributes,
    decimal? Valuation, [property: CodeList(CodeLists.ValuationBases, "code")] string? ValuationBasis, string? ValuationCurrency, string? Subdivision, string? PostalCode,
    [property: CodeList(CodeLists.RiskZones, "zone_code")] string? RiskZone, RiskObjectStatus Status, Guid OwnerPartyId)
{
    public static PortalRiskObjectView From(RiskObject r) => new(r.Id, r.ObjectType, System.Text.Json.JsonDocument.Parse(r.Attributes).RootElement.Clone(), r.Valuation,
        r.ValuationBasis, r.ValuationCurrency, r.Subdivision, r.PostalCode, r.RiskZone, r.Status, r.OwnerPartyId!.Value);
}

/// <summary>A peril a member can report a loss under (BR-093).</summary>
public sealed record PortalPerilView([property: CodeList(CodeLists.Perils, "peril_code")] string Code, string Label);

/// <summary>A loss cause a member can report, with the peril it falls under (BR-093).</summary>
public sealed record PortalLossCauseView([property: CodeList(CodeLists.LossCauses, "cause_code")] string Code, [property: CodeList(CodeLists.Perils, "peril_code")] string PerilCode, string Label);

/// <summary>The perils and loss causes in force for a policy's product (its line), for the member to choose from when notifying a loss (BR-093).</summary>
public sealed record PortalLossCodesView(IReadOnlyList<PortalPerilView> Perils, IReadOnlyList<PortalLossCauseView> Causes);

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

    /// <summary>
    /// Whether the broker services the party now (BR-092), by the party's General records: a live policy (Issued or In
    /// force) under one of its codes; with no live policy, the party's latest quote under one of them. Null when the party
    /// has neither, so the broker that registered it services it.
    /// </summary>
    Task<bool?> CurrentlyServices(ExternalSubject broker, Guid partyId, CancellationToken cancellationToken);

    /// <summary>The risk objects the party owns, by ID with cursor paging.</summary>
    Task<IReadOnlyList<RiskObject>> RiskObjects(Guid ownerPartyId, Guid? afterId, int limit, CancellationToken cancellationToken);
}

public sealed record ListPortalPolicies(Guid? AfterId, int Limit) : IQuery<IReadOnlyList<PortalPolicyView>>;

public sealed record GetPortalPolicy(Guid PolicyId) : IQuery<PortalPolicyView>;

public sealed record ListPortalQuotes(Guid? AfterId, int Limit) : IQuery<IReadOnlyList<PortalQuoteView>>;

public sealed record GetPortalQuote(Guid QuoteId) : IQuery<PortalQuoteView>;

public sealed record GetPortalClaim(Guid ClaimId) : IQuery<PortalClaimView>;

public sealed record ListPortalClaims(Guid? AfterId, int Limit) : IQuery<IReadOnlyList<PortalClaimView>>;

/// <summary>The loss codes for a policy of the member's (BR-093); any other policy is not found.</summary>
public sealed record GetPortalLossCodes(Guid PolicyId) : IQuery<PortalLossCodesView>;

/// <summary>A client's risk objects (BR-092), for a broker that services the client now; a client it does not is not found.</summary>
public sealed record ListPortalRiskObjects(Guid ClientPartyId, Guid? AfterId, int Limit) : IQuery<IReadOnlyList<PortalRiskObjectView>>;

/// <summary>A risk object whose owner the broker services now (BR-092); any other is not found.</summary>
public sealed record GetPortalRiskObject(Guid RiskObjectId) : IQuery<PortalRiskObjectView>;

/// <summary>
/// Before a broker registers a risk object (BR-092): the client is one it services now, or one it registered that has no
/// General records yet. Gives the party the item is registered for: a registration staff linked to an existing party is
/// registered for as that party, as quotes are.
/// </summary>
public sealed record PortalClientAllowed(Guid ClientPartyId) : IQuery<Guid>;

/// <summary>
/// Before a broker's quote (§3): the proposer is a client the broker services or one it registered. Gives the broker's own
/// intermediary code, which the quote is made under whatever the request says, and the proposer to quote for: a
/// registration staff linked to an existing party after review (§6) is quoted for as that party.
/// </summary>
/// <remarks>Each risk object quoted on belongs to the client (BR-092): a broker quotes only on the client's own items.</remarks>
public sealed record PortalQuoteAllowed(Guid ProposerPartyId, IReadOnlyList<Guid>? RiskObjectIds = null) : IQuery<PortalQuoter>;

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
    ITenantContext tenant, IGeneralCodes codes, ProductFactory.Contracts.ISellableProducts products, IBusinessDate businessDate) :
    IQueryHandler<ListPortalPolicies, IReadOnlyList<PortalPolicyView>>,
    IQueryHandler<GetPortalPolicy, PortalPolicyView>,
    IQueryHandler<ListPortalQuotes, IReadOnlyList<PortalQuoteView>>,
    IQueryHandler<GetPortalQuote, PortalQuoteView>,
    IQueryHandler<GetPortalClaim, PortalClaimView>,
    IQueryHandler<ListPortalClaims, IReadOnlyList<PortalClaimView>>,
    IQueryHandler<PortalQuoteAllowed, PortalQuoter>,
    IQueryHandler<PortalClaimAllowed, bool>,
    IQueryHandler<ListPortalRiskObjects, IReadOnlyList<PortalRiskObjectView>>,
    IQueryHandler<GetPortalRiskObject, PortalRiskObjectView>,
    IQueryHandler<PortalClientAllowed, Guid>,
    IQueryHandler<GetPortalLossCodes, PortalLossCodesView>
{
    private static readonly Error RiskObjectNotFound = Error.NotFound(ErrorCodes.NotFound, "The risk object was not found.");

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

        // BR-092: the items quoted on are the client's own, and the broker services the client now; an unknown item gets the
        // same answer as another's.
        foreach (var riskObjectId in query.RiskObjectIds ?? [])
        {
            if (await general.RiskObject(riskObjectId, cancellationToken) is not { OwnerPartyId: { } owner } || (owner != proposer && owner != query.ProposerPartyId)
                || !await CurrentlyServices(broker, owner, cancellationToken))
            {
                return Error.Forbidden(ErrorCodes.OutOfScope,
                    "You can quote only on your client's own risk objects; register the item for the client first (POST /portal/general/risk-objects).");
            }
        }

        return new PortalQuoter(code, proposer);
    }

    public async Task<Result<IReadOnlyList<PortalRiskObjectView>>> Handle(ListPortalRiskObjects query, CancellationToken cancellationToken) =>
        await CurrentlyServices(Related.Require(execution), query.ClientPartyId, cancellationToken)
            ? Result.Success<IReadOnlyList<PortalRiskObjectView>>((await queries.RiskObjects(query.ClientPartyId, query.AfterId, query.Limit, cancellationToken))
                .Select(PortalRiskObjectView.From).ToList())
            : Error.NotFound(ErrorCodes.NotFound, "The client was not found.");

    public async Task<Result<PortalRiskObjectView>> Handle(GetPortalRiskObject query, CancellationToken cancellationToken) =>
        await general.RiskObject(query.RiskObjectId, cancellationToken) is { OwnerPartyId: { } owner } risk
        && await CurrentlyServices(Related.Require(execution), owner, cancellationToken)
            ? PortalRiskObjectView.From(risk)
            : RiskObjectNotFound;

    /// <summary>
    /// BR-093: the perils of the policy's product line in force today (its class, or its family when it has no class) and
    /// their loss causes. A code retired, or of another line, is not listed.
    /// </summary>
    public async Task<Result<PortalLossCodesView>> Handle(GetPortalLossCodes query, CancellationToken cancellationToken)
    {
        if (await general.Policy(query.PolicyId, cancellationToken) is not { } policy || !Related.Policy(Related.Require(execution), policy))
        {
            return PolicyNotFound;
        }

        if (await products.Version(tenant.RequireTenantId(), policy.ProductCode, policy.ProductVersion, businessDate.Today, cancellationToken) is not { } product)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The policy's product version was not found.");
        }

        var (perils, causes) = await codes.LossCodes(product.Line, businessDate.Today, cancellationToken);
        return new PortalLossCodesView(perils.Select(p => new PortalPerilView(p.Code, p.Label)).ToList(),
            causes.Select(c => new PortalLossCauseView(c.Code, c.Peril, c.Label)).ToList());
    }

    public async Task<Result<Guid>> Handle(PortalClientAllowed query, CancellationToken cancellationToken)
    {
        if (!await CurrentlyServices(Related.Require(execution), query.ClientPartyId, cancellationToken))
        {
            return Error.Forbidden(ErrorCodes.OutOfScope, "You can register an item only for a client you service, or one you registered.");
        }

        return await parties.MergedInto(tenant.RequireTenantId(), query.ClientPartyId, cancellationToken) ?? query.ClientPartyId;
    }

    /// <summary>
    /// BR-092 (decided by Ade): a broker services a client now when the client's live General policy, or with none its latest
    /// quote, is under one of the broker's codes; a client with neither is serviced by the broker that registered it. So a
    /// broker loses a client's items when the client's business moves to another intermediary.
    /// </summary>
    private async Task<bool> CurrentlyServices(ExternalSubject broker, Guid partyId, CancellationToken cancellationToken)
    {
        if (await queries.CurrentlyServices(broker, partyId, cancellationToken) is { } byRecords)
        {
            return byRecords;
        }

        var registeredBy = await parties.RegisteredBy(tenant.RequireTenantId(), partyId, cancellationToken);
        return registeredBy is not null && registeredBy.StartsWith($"{ExternalSubject.Intermediary}:", StringComparison.Ordinal)
            && broker.Brokers(registeredBy[(ExternalSubject.Intermediary.Length + 1)..]);
    }

    public async Task<Result<bool>> Handle(PortalClaimAllowed query, CancellationToken cancellationToken) =>
        await general.Policy(query.PolicyId, cancellationToken) is { } policy && Related.Policy(Related.Require(execution), policy) ? true : PolicyNotFound;
}
