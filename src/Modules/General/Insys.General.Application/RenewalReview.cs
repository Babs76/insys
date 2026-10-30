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
using FluentValidation;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
using Insys.General.Contracts;
using Insys.General.Domain;
using Insys.General.Domain.Policies;
using Insys.General.Domain.Renewals;
using Insys.General.Domain.Underwriting;
using Insys.ProductFactory.Contracts;
using Microsoft.Extensions.Logging;

namespace Insys.General.Application;

/// <summary>Command ReviewRenewal (Wave 4 §4.2): opens the review of a policy's renewal, from the daily job.</summary>
public sealed record ReviewRenewal(Guid PolicyId, DateOnly Day) : ICommand<bool>;

/// <summary>Command DecideRenewal (F019): an underwriter offers a referred or ineligible renewal, with any loading, or does not.</summary>
public sealed record DecideRenewal(Guid RenewalId, bool Offer, [property: CodeList(CodeLists.UnderwritingDecisions, "code")] string DecisionCode, decimal? LoadingPct)
    : ICommand<RenewalView>;

public sealed record GetRenewal(Guid RenewalId) : IQuery<RenewalView>;

public sealed record ListRenewals(RenewalStatus? Status, Guid? PolicyId, Guid? BeforeId, int Limit) : IQuery<IReadOnlyList<RenewalView>>;

public sealed record RenewalCheckView(
    [property: OneOf("PRODUCT_ON_SALE", "DUE_DILIGENCE", "FRAUD_REFERRALS", "CLAIMS_HISTORY", "RISK_CHANGES", "ELIGIBILITY_RULES", "UNDERWRITING_RULES", "RE_RATING")]
    string CheckCode, [property: OneOf("PASS", "REFER", "FAIL", "PENDING")] string Outcome, string? Detail);

/// <summary>
/// A renewal and its review (Wave 4 §4.2): the claims history and risk changes it read, the checks and their results, the
/// re-rating (renewal quote, rated premium, claims experience factor), any loading, and the offered premium.
/// </summary>
public sealed record RenewalView(Guid Id, Guid PolicyId, string PolicyNumber, string ProductCode, DateOnly PeriodEnd, RenewalStatus Status, DateOnly OpenedOn,
    string Currency, decimal CurrentPremium, decimal SumInsured, int ClaimsCount, decimal ClaimsPaid, decimal EarnedPremium, decimal? LossRatio, int Endorsements,
    int FraudReferredClaims, decimal Factor, bool ReRated, Guid? QuoteId, decimal? RatedPremium, string? ReferralReason, decimal? LoadingPct, decimal? OfferedPremium,
    bool ChangedTerms, [property: CodeList(CodeLists.UnderwritingDecisions, "code")] string? DecisionCode, Guid? DecidedBy, DateTimeOffset? DecidedAt,
    IReadOnlyList<RenewalCheckView> Checks, long RowVersion, [property: AlwaysPresent] RenewalAnswerView? Answer = null,
    [property: AlwaysPresent] NonRenewalView? NonRenewal = null)
{
    public static RenewalView From(Renewal r) => new(r.Id, r.PolicyId, r.PolicyNumber, r.ProductCode, r.PeriodEnd, r.Status, r.OpenedOn, r.Currency, r.CurrentPremium,
        r.SumInsured, r.ClaimsCount, r.ClaimsPaid, r.EarnedPremium, r.LossRatio, r.Endorsements, r.FraudReferredClaims, r.Factor, r.ReRated, r.QuoteId, r.RatedPremium,
        r.ReferralReason, r.LoadingPct, r.OfferedPremium, r.ChangedTerms, r.DecisionCode, r.DecidedBy, r.DecidedAt,
        r.Checks.Select(c => new RenewalCheckView(c.CheckCode, c.Outcome, c.Detail)).ToList(), r.RowVersion,
        r.Acceptance is null ? null : new RenewalAnswerView(r.Acceptance, r.NeedsAcceptance, r.AcceptedAt, r.AcceptedBy, r.AcceptedChannel, r.DeclinedAt, r.DeclinedBy,
            r.DeclinedChannel, r.RenewalPolicyId),
        r.NonRenewalStatus is null ? null : new NonRenewalView(r.NonRenewalReason!, r.NonRenewalStatus, r.NonRenewalRequestedBy, r.NonRenewalRequestedAt,
            r.NonRenewalDecidedBy, r.NonRenewalDecidedAt, r.NonRenewalNoticeOn, r.CoverExtendedTo));
}

/// <summary>Wave 4 §4.4 (OQ-130): how the renewal is accepted, whether it needs the customer's acceptance, and their answer, by whom and through which channel.</summary>
public sealed record RenewalAnswerView([property: OneOf("AUTOMATIC", "ON_ACCEPTANCE", "ON_PAYMENT")] string Acceptance, bool NeedsAcceptance, DateTimeOffset? AcceptedAt,
    Guid? AcceptedBy, [property: OneOf("STAFF", "PORTAL", "BROKER", "PAYMENT")] string? AcceptedChannel, DateTimeOffset? DeclinedAt, Guid? DeclinedBy,
    [property: OneOf("STAFF", "PORTAL", "BROKER")] string? DeclinedChannel, Guid? RenewalPolicyId);

/// <summary>Wave 4 §4.3 (F025, OQ-132): an insurer's non-renewal: its reason, approval, notice and any cover extended for the notice.</summary>
public sealed record NonRenewalView([property: CodeList(CodeLists.NonRenewalReasons, "reason_code")] string ReasonCode,
    [property: OneOf("REQUESTED", "DECIDED", "REJECTED")] string Status, Guid? RequestedBy, DateTimeOffset? RequestedAt, Guid? DecidedBy, DateTimeOffset? DecidedAt,
    DateOnly? NoticeOn, DateOnly? CoverExtendedTo);

public sealed class DecideRenewalValidator : AbstractValidator<DecideRenewal>
{
    public DecideRenewalValidator()
    {
        RuleFor(c => c.DecisionCode).NotEmpty().MaximumLength(15);
        RuleFor(c => c.LoadingPct).InclusiveBetween(0m, 500m).When(c => c.LoadingPct is not null);
    }
}

/// <summary>
/// Opens a policy's renewal review (§4.2). It reads the claims of the policy and the policies it renewed over the product's
/// lookback, the premium earned over it (the loss ratio's base), this period's endorsements, the claims referred for fraud,
/// whether the product is on sale for the renewal period and the policyholder cleared for issue, and the claims experience
/// factor for a product that re-rates. Rule sets and the re-rating follow through Rules and Rating.
/// </summary>
internal sealed class ReviewRenewalHandler(IGeneralRepository general, IGeneralCodes codes, ISellableProducts products, Party.Contracts.IDueDiligenceStatus dueDiligence,
    ClaimsFramework.Contracts.IClaimFraudReferrals fraud, ITenantContext tenant, IClock clock) : ICommandHandler<ReviewRenewal, bool>
{
    public async Task<Result<bool>> Handle(ReviewRenewal command, CancellationToken cancellationToken)
    {
        if (await general.Policy(command.PolicyId, cancellationToken) is not { Status: PolicyStatus.InForce } policy
            || await general.RenewalOfPeriod(policy.Id, policy.PeriodEnd, cancellationToken) is not null)
        {
            return false;
        }

        var day = command.Day;
        if (await codes.RenewalRule(policy.ProductCode, day, cancellationToken) is not { Review: true } rule
            || await codes.RenewalReviewRule(policy.ProductCode, day, cancellationToken) is not { } review)
        {
            return Error.Validation(GeneralErrorCodes.CodeNotInForce, $"{policy.ProductCode} has no renewal review rule in force.");
        }

        // The policy and the policies it renewed, back over the lookback.
        var windowStart = policy.PeriodEnd.AddYears(-review.ClaimsLookbackYears).AddDays(1);
        var chain = new List<GeneralPolicy> { policy };
        while (chain[^1].RenewalOfId is { } previous && chain[^1].PeriodStart > windowStart && await general.Policy(previous, cancellationToken) is { } older)
        {
            chain.Add(older);
        }

        var ids = chain.Select(p => p.Id).ToList();
        var claims = (await general.ClaimHistory(ids, cancellationToken))
            .Where(c => c.LossDate >= windowStart && c.Status != Domain.Claims.ClaimStatus.FirstNotification).ToList();
        var earned = chain.Sum(p => Earned(p, windowStart, day));
        var tenantId = tenant.RequireTenantId();
        var onSale = await products.Sellable(tenantId, policy.ProductCode, null, policy.PeriodEnd.AddDays(1), cancellationToken) is { Domain: "GENERAL" };
        var cleared = (await dueDiligence.ClearedFor(tenantId, policy.PolicyholderPartyId, Party.Contracts.DueDiligenceSteps.Issue, "GENERAL", cancellationToken)).Cleared;
        var paid = claims.Sum(c => c.Paid);
        var lossRatio = earned > 0 ? paid / earned : (decimal?)null;
        var factor = rule.ReRating ? await codes.ClaimsFactor(policy.ProductCode, claims.Count, lossRatio, day, cancellationToken) : 1m;
        var facts = new RenewalFacts(policy.AnnualPremium, policy.Currency, policy.Risks.Where(r => r.DetachedOn is null).Sum(r => r.SumInsured), claims.Count, paid,
            Math.Round(earned, 2, MidpointRounding.AwayFromZero), await general.EndorsementCount(policy.Id, cancellationToken),
            await fraud.ReferredClaims(tenantId, GeneralRatingSubjects.SourceDomain, ids, cancellationToken), onSale, cleared, factor, rule.ReRating,
            review.EligibilityRuleset, review.UnderwritingRules);
        general.Add(Renewal.OpenReview(Guid.CreateVersion7(), policy.Id, policy.PolicyNumber, policy.ProductCode, policy.PeriodEnd, facts, day, clock.UtcNow));
        return true;
    }

    /// <summary>The premium a policy earned in the window up to the day: its annual premium for the share of its period inside both.</summary>
    private static decimal Earned(GeneralPolicy policy, DateOnly windowStart, DateOnly day)
    {
        var from = policy.PeriodStart > windowStart ? policy.PeriodStart : windowStart;
        var to = policy.PeriodEnd < day ? policy.PeriodEnd : day;
        return to < from ? 0m : policy.AnnualPremium * (to.DayNumber - from.DayNumber + 1) / policy.PeriodDays;
    }
}

/// <summary>
/// The review's later steps (§4.2): Rules' decisions on its rule sets, and its renewal quote, created from the policy's
/// risks for the renewal period and priced as new business (BR-095's pricing). A quote that cannot be priced refers the
/// renewal to an underwriter.
/// </summary>
public sealed class RenewalReviewSteps(IGeneralRepository general, ISellableProducts products, ITenantContext tenant, IBusinessDate businessDate, IClock clock,
    ILogger<RenewalReviewSteps> logger) :
    IIntegrationEventHandler<Rules.Contracts.RuleDecisionRecorded>,
    IIntegrationEventHandler<GeneralRenewalRatingRequested>
{
    public async Task Handle(Rules.Contracts.RuleDecisionRecorded decided, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (decided.SubjectType != RenewalSubjects.SubjectType || await general.Renewal(decided.SubjectId, cancellationToken) is not { } renewal)
        {
            return;
        }

        var result = decided.RulesetCode.StartsWith("UW:", StringComparison.Ordinal)
            ? renewal.RecordUnderwriting(decided.DecisionId, decided.Outcome, decided.FiredRules, clock.UtcNow)
            : renewal.RecordEligibility(decided.DecisionId, decided.Outcome, clock.UtcNow);
        if (result.IsFailure)
        {
            logger.LogWarning("Rule decision {DecisionId} not attached to renewal {RenewalId}: {Code}", decided.DecisionId, renewal.Id, result.Error.Code);
        }
    }

    public async Task Handle(GeneralRenewalRatingRequested requested, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (await general.Renewal(requested.RenewalId, cancellationToken) is not { Status: RenewalStatus.Reviewing, QuoteId: null } renewal
            || await general.Policy(requested.PolicyId, cancellationToken) is not { } policy)
        {
            return;
        }

        var start = policy.PeriodEnd.AddDays(1);
        var product = await Products.Sellable(products, tenant.RequireTenantId(), policy.ProductCode, null, start, cancellationToken);
        var risks = policy.Risks.Where(r => r.DetachedOn is null)
            .Select(r => new RiskRequest(r.RiskObjectId, r.SumInsured, r.Coverages.Select(c => new CoverageRequest(c.CoverageCode, c.SumInsured, c.ExcessScheduleCode)).ToList()))
            .ToList();
        var id = Guid.CreateVersion7();
        var quote = product.IsSuccess
            ? GeneralQuote.CreateQuote(id, References.Of("GQ-", id, 15), null, product.Value, policy.PolicyholderPartyId, policy.IntermediaryCode, policy.Currency, start,
                GeneralPolicy.NextPeriodEnd(policy.PeriodStart, policy.PeriodEnd, start), risks, policy.PeriodEnd)
            : product.Error;
        if (quote.IsFailure)
        {
            renewal.RecordRatingIssue(Guid.Empty, $"The renewal could not be quoted: {quote.Error.Message}", clock.UtcNow);
            return;
        }

        quote.Value.PlaceAt(policy.OrgNodeCode);
        quote.Value.LinkRenewal(renewal.Id);
        renewal.LinkQuote(quote.Value.Id);
        var objects = new List<RiskObject>();
        foreach (var risk in risks)
        {
            if (await general.RiskObject(risk.RiskObjectId, cancellationToken) is { } riskObject)
            {
                objects.Add(riskObject);
            }
        }

        var values = QuotePricing.Values(objects, risks.Sum(r => r.SumInsured));
        if (values.IsSuccess)
        {
            quote.Value.RequestPricing(values.Value, businessDate.Today, clock.UtcNow);
        }
        else
        {
            quote.Value.RecordRatingIssue(values.Error.Code, values.Error.Message);
            renewal.RecordRatingIssue(quote.Value.Id, values.Error.Message, clock.UtcNow);
        }

        general.Add(quote.Value);
    }
}

/// <summary>Command DecideRenewal (F019), under the underwriter's authority for the product and the sum insured, as a quote referral is decided.</summary>
internal sealed class DecideRenewalHandler(IGeneralRepository general, IGeneralCodes codes, IExecutionContext execution, IBusinessDate businessDate, IClock clock,
    IAuditTrail audit, Identity.Contracts.IUserRoleQuery roles, Rules.Contracts.IUnderwritingAuthorities authorities, ITenantContext tenant)
    : ICommandHandler<DecideRenewal, RenewalView>
{
    public async Task<Result<RenewalView>> Handle(DecideRenewal command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (await general.Renewal(command.RenewalId, cancellationToken) is not { } renewal)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The renewal was not found.");
        }

        if (!await codes.Exists(GeneralCode.UnderwritingDecision, command.DecisionCode, businessDate.Today, cancellationToken))
        {
            return Error.Validation(GeneralErrorCodes.CodeNotInForce, $"{command.DecisionCode} is not an underwriting decision code in force.");
        }

        var tenantId = tenant.RequireTenantId();
        var authority = (await authorities.For(tenantId, await roles.EffectiveRoles(tenantId, user.Value, cancellationToken), renewal.ProductCode, businessDate.Today,
                cancellationToken))
            .FirstOrDefault(a => a.MaxSumAssured >= renewal.SumInsured && a.DecisionTypes.Contains(command.DecisionCode, StringComparer.Ordinal));
        if (authority is null)
        {
            return Error.Forbidden(GeneralErrorCodes.UnderwritingAuthorityExceeded,
                $"Your underwriting authority does not cover a {command.DecisionCode} decision on {renewal.ProductCode} for {renewal.SumInsured} {renewal.Currency}.");
        }

        audit.Record(new AuditEntry("GENERAL", "DecideRenewal", nameof(Renewal), renewal.Id.ToString(),
            new Dictionary<string, string?> { ["offer"] = command.Offer.ToString(), ["decision"] = command.DecisionCode, ["authority"] = authority.RoleCode }));
        var result = renewal.DecideReferral(command.Offer, command.DecisionCode, command.LoadingPct, user.Value, clock.UtcNow);
        return result.IsFailure ? result.Error : RenewalView.From(renewal);
    }
}

internal sealed class RenewalQueries(IGeneralRepository general) : IQueryHandler<GetRenewal, RenewalView>, IQueryHandler<ListRenewals, IReadOnlyList<RenewalView>>
{
    public async Task<Result<RenewalView>> Handle(GetRenewal query, CancellationToken cancellationToken) =>
        await general.Renewal(query.RenewalId, cancellationToken) is { } renewal ? RenewalView.From(renewal) : Error.NotFound(ErrorCodes.NotFound, "The renewal was not found.");

    public async Task<Result<IReadOnlyList<RenewalView>>> Handle(ListRenewals query, CancellationToken cancellationToken) =>
        (await general.Renewals(query.Status, query.PolicyId, query.BeforeId, query.Limit, cancellationToken)).Select(RenewalView.From).ToList();
}
