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
using Insys.BuildingBlocks.Domain;

namespace Insys.General.Domain.Underwriting;

/// <summary>States from domain-model.json (general › GeneralQuote).</summary>
public enum QuoteStatus
{
    Draft,
    Rated,
    Referred,
    Issued,
    Accepted,
    Rejected,
    Expired,
    Declined,
}

public enum QuoteTrigger
{
    RateQuote,
    ReferQuote,
    ReferralApproved,
    ReferralDeclined,
    IssueQuote,
    AcceptQuote,
    RejectQuote,
    ValidityEnds,
}

public sealed record GeneralQuoteIssued(Guid QuoteId, string QuoteNumber, int QuoteVersion, string ProductCode, int ProductVersion, decimal Premium, string Currency,
    DateOnly ValidUntil, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record GeneralQuoteAccepted(Guid QuoteId, string QuoteNumber, int QuoteVersion, Guid ProposerPartyId, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record GeneralQuoteExpired(Guid QuoteId, string QuoteNumber, int QuoteVersion, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// A quote priced as it is made (BR-095) asks Rating for its premium: the rating inputs are the risk objects' attributes and
/// the quote's sum insured, as JSON, rated annually on the day the quote was made.
/// </summary>
public sealed record GeneralQuotePricingRequested(Guid QuoteId, string QuoteNumber, int QuoteVersion, string ProductCode, int ProductVersion, string Currency,
    DateOnly RatingDate, string ValuesJson, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>The quote was referred to an underwriter (by hand or by the rules), with its total sum insured, so the referral reaches someone with the authority.</summary>
public sealed record GeneralQuoteReferred(Guid QuoteId, string QuoteNumber, int QuoteVersion, string ProductCode, string ReasonCode, decimal SumInsured, string Currency,
    DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>An underwriter decided the quote's referral.</summary>
public sealed record GeneralQuoteReferralDecided(Guid QuoteId, string QuoteNumber, int QuoteVersion, bool Approved, string DecisionCode, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>The underwriter declined the referred quote: it is final, and the proposer can be told why.</summary>
public sealed record GeneralQuoteDeclined(Guid QuoteId, string QuoteNumber, int QuoteVersion, string DecisionCode, string Reason, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>A coverage asked for on a risk: its sum insured and excess schedule.</summary>
public sealed record CoverageRequest(string CoverageCode, decimal SumInsured, string? ExcessScheduleCode);

/// <summary>A risk object to insure, with its sum insured and coverages.</summary>
public sealed record RiskRequest(Guid RiskObjectId, decimal SumInsured, IReadOnlyList<CoverageRequest> Coverages);

/// <summary>A limit of the product (product.cfg_limit_and_deductible_schedules, §7.2) for a coverage.</summary>
public sealed record CoverageLimit(string ScheduleCode, string CoverageCode, string LimitType, decimal? Amount, decimal? Percentage, string Rule, string Currency);

/// <summary>What a product version allows: its coverages and their limits and sublimits, and when cover starts (ISSUE or FIRST_PAYMENT).</summary>
public sealed record ProductTerms(string ProductCode, int Version, string Line, IReadOnlyList<string> Coverages, IReadOnlyList<CoverageLimit> Limits,
    string CoverStartsOn = "ISSUE")
{
    /// <summary>Coverages are the product's, and stay within its limits and sublimits (§7.2), and excess schedules are the coverage's.</summary>
    public Result Check(IEnumerable<CoverageRequest> coverages, string currency)
    {
        var problems = new List<string>();
        foreach (var coverage in coverages)
        {
            if (!Coverages.Contains(coverage.CoverageCode, StringComparer.Ordinal))
            {
                problems.Add($"{coverage.CoverageCode} is not a coverage of {ProductCode} v{Version}.");
                continue;
            }

            foreach (var limit in Limits.Where(l => l.CoverageCode == coverage.CoverageCode && l.LimitType is "LIMIT" or "SUBLIMIT" && l.Amount is not null
                         && l.Currency == currency))
            {
                if (coverage.SumInsured > limit.Amount)
                {
                    problems.Add($"{coverage.CoverageCode} sum insured {coverage.SumInsured} {currency} exceeds the {limit.LimitType.ToLowerInvariant()} of {limit.Amount} ({limit.ScheduleCode}).");
                }
            }

            if (coverage.ExcessScheduleCode is { } schedule && !Limits.Any(l => l.ScheduleCode == schedule && l.CoverageCode == coverage.CoverageCode))
            {
                problems.Add($"{schedule} is not an excess schedule of {coverage.CoverageCode}.");
            }
        }

        return problems.Count == 0 ? Result.Success() : Error.Validation(GeneralErrorCodes.OutsideProductLimits, string.Join(" ", problems));
    }
}

/// <summary>Entity QuotedCoverage (general.quote_coverage, V0016).</summary>
public sealed class QuotedCoverage : Entity<Guid>, ITenantScoped
{
    private QuotedCoverage()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid QuoteRiskId { get; private set; }

    public string CoverageCode { get; private set; } = "";

    public decimal SumInsured { get; private set; }

    public string? ExcessScheduleCode { get; private set; }

    internal static QuotedCoverage Create(Guid riskId, CoverageRequest request) => new()
    {
        Id = Guid.CreateVersion7(), QuoteRiskId = riskId, CoverageCode = Guard.MaxLength(request.CoverageCode, 20), SumInsured = request.SumInsured,
        ExcessScheduleCode = request.ExcessScheduleCode,
    };
}

/// <summary>Entity QuotedRisk (general.quote_risk, V0016).</summary>
public sealed class QuotedRisk : Entity<Guid>, ITenantScoped
{
    private readonly List<QuotedCoverage> _coverages = [];

    private QuotedRisk()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid QuoteId { get; private set; }

    public Guid RiskObjectId { get; private set; }

    public decimal SumInsured { get; private set; }

    public IReadOnlyList<QuotedCoverage> Coverages => _coverages;

    internal static QuotedRisk Create(Guid quoteId, RiskRequest request)
    {
        var risk = new QuotedRisk { Id = Guid.CreateVersion7(), QuoteId = quoteId, RiskObjectId = Guard.NotEmpty(request.RiskObjectId), SumInsured = request.SumInsured };
        risk._coverages.AddRange(request.Coverages.Select(c => QuotedCoverage.Create(risk.Id, c)));
        return risk;
    }

    public RiskRequest ToRequest() =>
        new(RiskObjectId, SumInsured, _coverages.Select(c => new CoverageRequest(c.CoverageCode, c.SumInsured, c.ExcessScheduleCode)).ToList());
}

/// <summary>Entity UnderwritingReferral (general.underwriting_referral): a quote referred to an underwriter, and the decision.</summary>
public sealed class UnderwritingReferral : Entity<Guid>, ITenantScoped
{
    private UnderwritingReferral()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid QuoteId { get; private set; }

    public string ReasonCode { get; private set; } = "";

    /// <summary>OPEN, APPROVED or DECLINED.</summary>
    public string Status { get; private set; } = "OPEN";

    public Guid? ReferredBy { get; private set; }

    public Guid? DecidedBy { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    public string? DecisionCode { get; private set; }

    public string[]? SpecialConditions { get; private set; }

    /// <summary>Why a declined referral was declined.</summary>
    public string? DeclineReason { get; private set; }

    public long RowVersion { get; private set; }

    internal static UnderwritingReferral Open(Guid quoteId, string reasonCode, Guid? referredBy) =>
        new() { Id = Guid.CreateVersion7(), QuoteId = quoteId, ReasonCode = Guard.MaxLength(reasonCode, 15), ReferredBy = referredBy };

    internal void Decide(bool approved, string decisionCode, IReadOnlyList<string> conditions, string? declineReason, Guid decidedBy, DateTimeOffset now)
    {
        Status = approved ? "APPROVED" : "DECLINED";
        DeclineReason = approved ? null : declineReason;
        DecisionCode = Guard.MaxLength(decisionCode, 15);
        SpecialConditions = conditions.Count == 0 ? null : conditions.ToArray();
        DecidedBy = decidedBy;
        DecidedAt = now;
    }
}

/// <summary>
/// The GeneralQuote aggregate (general.quote): versioned (a change is a new version with the same number); rated only
/// from a recorded rating request (PRD-008); accepted only within its validity period (GEN-001).
/// </summary>
public sealed class GeneralQuote : AggregateRoot<Guid>
{
    public static readonly StateMachine<QuoteStatus, QuoteTrigger> Lifecycle = new StateMachine<QuoteStatus, QuoteTrigger>()
        .Allow(QuoteStatus.Draft, QuoteTrigger.RateQuote, QuoteStatus.Rated)
        .Allow(QuoteStatus.Rated, QuoteTrigger.ReferQuote, QuoteStatus.Referred)
        .Allow(QuoteStatus.Referred, QuoteTrigger.ReferralApproved, QuoteStatus.Rated)
        .Allow(QuoteStatus.Referred, QuoteTrigger.ReferralDeclined, QuoteStatus.Declined)
        .Allow(QuoteStatus.Rated, QuoteTrigger.IssueQuote, QuoteStatus.Issued)
        .Allow(QuoteStatus.Issued, QuoteTrigger.AcceptQuote, QuoteStatus.Accepted)
        .Allow(QuoteStatus.Issued, QuoteTrigger.RejectQuote, QuoteStatus.Rejected)
        .Allow(QuoteStatus.Issued, QuoteTrigger.ValidityEnds, QuoteStatus.Expired);

    private readonly List<QuotedRisk> _risks = [];
    private readonly List<UnderwritingReferral> _referrals = [];

    private GeneralQuote()
    {
    }

    public string QuoteNumber { get; private set; } = "";

    public int QuoteVersion { get; private set; }

    public string ProductCode { get; private set; } = "";

    /// <summary>The organisation node (normally a branch) that owns the record (F103, addenda/access-scopes.md §2); its children inherit it.</summary>
    public string? OrgNodeCode { get; private set; }

    /// <summary>The channel the record was created through (F005, addenda/channel-permissions.md §3); null when the tenant has no channels.</summary>
    public string? ChannelCode { get; private set; }

    /// <summary>Records the channel of the command that created the record, once (F005).</summary>
    public void RecordChannel(string? channelCode)
    {
        ChannelCode ??= channelCode is null ? null : Guard.MaxLength(channelCode, 12);
    }

    /// <summary>Places a new quote at its organisation node (§2). A later version keeps its first version's node.</summary>
    public void PlaceAt(string? orgNodeCode)
    {
        OrgNodeCode ??= orgNodeCode is null ? null : Guard.MaxLength(orgNodeCode, 15);
    }

    public int ProductVersion { get; private set; }

    public Guid ProposerPartyId { get; private set; }

    public string? IntermediaryCode { get; private set; }

    public QuoteStatus Status { get; private set; }

    public DateOnly ValidUntil { get; private set; }

    public DateOnly PeriodStart { get; private set; }

    public DateOnly PeriodEnd { get; private set; }

    /// <summary>The annual premium of the recorded rating request. TODO(spec): the rating result is taken as the annual premium (OQ-059).</summary>
    public decimal? Premium { get; private set; }

    public string Currency { get; private set; } = "";

    /// <summary>The recorded premium calculation (rating.premium_calculation) the premium comes from (PRD-008).</summary>
    public Guid? RatingRequestId { get; private set; }

    public int? RatingVersion { get; private set; }

    /// <summary>
    /// A broker's portal quote, and every later version of it, is priced as it is made (BR-095, decided by Ade): General asks
    /// Rating, and the quote reads Rated once the premium comes back. Nobody requests its calculation by hand.
    /// </summary>
    public bool PricedOnCreation { get; private set; }

    /// <summary>
    /// The billing frequency the quote is priced in, when its product allows the one chosen (general.cfg_product_billing_frequencies;
    /// Ade, 28 October 2026); null means annual. The premium stays annual; the instalment is shown beside it.
    /// </summary>
    public string? PremiumFrequency { get; private set; }

    /// <summary>The frequency's periods per year when the quote was made (billing.ref_billing_frequencies).</summary>
    public int? PremiumPeriods { get; private set; }

    /// <summary>The product's instalment loading for the frequency when the quote was made, in per cent (0 when none).</summary>
    public decimal? PremiumLoadingPct { get; private set; }

    /// <summary>What each instalment in <see cref="PremiumFrequency"/> comes to: the annual premium over the periods, with the loading, to the cent.</summary>
    public decimal? InstalmentPremium => Premium is { } annual && PremiumPeriods is { } periods
        ? Math.Round(annual / periods * (1 + (PremiumLoadingPct ?? 0m) / 100m), 2, MidpointRounding.AwayFromZero)
        : null;

    /// <summary>Why a quote priced as it is made could not be rated (a Rating error code), while it stays Draft; null once rated.</summary>
    public string? RatingIssueCode { get; private set; }

    /// <summary>The reason in words, for the broker.</summary>
    public string? RatingIssue { get; private set; }

    public IReadOnlyList<QuotedRisk> Risks => _risks;

    public IReadOnlyList<UnderwritingReferral> Referrals => _referrals;

    /// <summary>Whether an underwriter approved a referral for the reason (F062: a quote over a zone limit issues once approved).</summary>
    public bool ReferralApproved(string reasonCode) => _referrals.Any(r => r.ReasonCode == reasonCode && r.Status == "APPROVED");

    /// <summary>Command CreateQuote: version 1 of a new quote number, or the next version of an existing one (<paramref name="previous"/>).</summary>
    public static Result<GeneralQuote> CreateQuote(Guid id, string quoteNumber, GeneralQuote? previous, ProductTerms product, Guid proposer, string? intermediary,
        string currency, DateOnly periodStart, DateOnly periodEnd, IReadOnlyList<RiskRequest> risks, DateOnly validUntil)
    {
        if (risks.Count == 0 || risks.Any(r => r.Coverages.Count == 0 || r.SumInsured <= 0 || r.Coverages.Any(c => c.SumInsured <= 0))
            || risks.Select(r => r.RiskObjectId).Distinct().Count() != risks.Count || risks.Any(r => r.Coverages.Select(c => c.CoverageCode).Distinct().Count() != r.Coverages.Count))
        {
            return Error.Validation(ErrorCodes.ValidationFailed,
                "A quote insures at least one risk object, each once, with at least one coverage each, and positive sums insured (GEN-002).");
        }

        if (periodEnd < periodStart)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "The policy period ends on or after the day it starts; its end date is the last day of cover.");
        }

        var limits = product.Check(risks.SelectMany(r => r.Coverages), currency);
        if (limits.IsFailure)
        {
            return limits.Error;
        }

        if (previous is not null && (previous.ProductCode != product.ProductCode))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "A new version of a quote is for the same product.");
        }

        var quote = new GeneralQuote
        {
            Id = Guard.NotEmpty(id),
            QuoteNumber = previous?.QuoteNumber ?? Guard.MaxLength(quoteNumber, 15),
            OrgNodeCode = previous?.OrgNodeCode,
            ChannelCode = previous?.ChannelCode,
            QuoteVersion = (previous?.QuoteVersion ?? 0) + 1,
            ProductCode = Guard.MaxLength(product.ProductCode, 15),
            ProductVersion = product.Version,
            ProposerPartyId = Guard.NotEmpty(proposer),
            IntermediaryCode = intermediary,
            Currency = Guard.MaxLength(currency, 3),
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            ValidUntil = validUntil,
            Status = QuoteStatus.Draft,
            PricedOnCreation = previous?.PricedOnCreation ?? false,
        };
        quote._risks.AddRange(risks.Select(r => QuotedRisk.Create(id, r)));
        quote.MarkChanged();
        return quote;
    }

    /// <summary>
    /// Command RateQuote, from Rating's PremiumCalculated for this quote: the premium always matches a recorded rating
    /// request (PRD-008) for the quote's product version and currency. A repeat of the same calculation changes nothing.
    /// </summary>
    public Result RateQuote(Guid calculationId, string productCode, int productVersion, int ratingVersion, decimal premium, string currency)
    {
        if (RatingRequestId == calculationId)
        {
            return Result.Success();
        }

        if (productCode != ProductCode || productVersion != ProductVersion || currency != Currency || premium < 0)
        {
            return Error.Validation(GeneralErrorCodes.RatingMismatch,
                $"The calculation is for {productCode} v{productVersion} in {currency}; quote {QuoteNumber} v{QuoteVersion} is for {ProductCode} v{ProductVersion} in {Currency}.");
        }

        var result = Fire(QuoteTrigger.RateQuote);
        if (result.IsSuccess)
        {
            Premium = premium;
            RatingRequestId = calculationId;
            RatingVersion = ratingVersion;
            RatingIssueCode = null;
            RatingIssue = null;
        }

        return result;
    }

    /// <summary>Prices a draft in a billing frequency its product allows, with the product's instalment loading for it: the premium stays annual, and the instalment is shown beside it.</summary>
    public Result PriceIn(string frequency, int periodsPerYear, decimal loadingPct = 0m)
    {
        if (Status != QuoteStatus.Draft || periodsPerYear <= 0 || loadingPct < 0)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Quote {QuoteNumber} v{QuoteVersion} is priced in a frequency while it is a draft, with a positive number of periods.");
        }

        PremiumFrequency = Guard.MaxLength(frequency, 20);
        PremiumPeriods = periodsPerYear;
        PremiumLoadingPct = loadingPct;
        MarkChanged();
        return Result.Success();
    }

    /// <summary>
    /// Command RequestPricing (BR-095): a quote priced as it is made asks Rating for its premium, with the inputs General
    /// gathered. Its later versions are priced the same way.
    /// </summary>
    public Result RequestPricing(string valuesJson, DateOnly ratingDate, DateTimeOffset now)
    {
        if (Status != QuoteStatus.Draft)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Quote {QuoteNumber} v{QuoteVersion} is {Status}; only a draft is priced.");
        }

        PricedOnCreation = true;
        MarkChanged();
        Raise(new GeneralQuotePricingRequested(Id, QuoteNumber, QuoteVersion, ProductCode, ProductVersion, Currency, ratingDate, valuesJson, now));
        return Result.Success();
    }

    /// <summary>
    /// Why the quote could not be priced (BR-095): it stays Draft with the reason, and is still priced as it is made. A
    /// quote already rated, referred or further on keeps its state, and a late failure is not recorded on it.
    /// </summary>
    public Result RecordRatingIssue(string code, string reason)
    {
        if (Status != QuoteStatus.Draft)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Quote {QuoteNumber} v{QuoteVersion} is {Status}; a rating issue is recorded on a draft only.");
        }

        PricedOnCreation = true;
        RatingIssueCode = Guard.MaxLength(code, 50);
        RatingIssue = reason.Length > 500 ? reason[..500] : reason;
        MarkChanged();
        return Result.Success();
    }

    /// <summary>
    /// A referral the rules raise, with nobody as its referrer, so any underwriter may decide it (BR-037): a quote priced as
    /// it is made that is over a zone and peril limit (F062) is referred as soon as it is rated (BR-095).
    /// </summary>
    public Result ReferQuoteByRules(string reasonCode, DateTimeOffset now) => Refer(reasonCode, null, now);

    /// <summary>Command ReferQuote: to an underwriter, with a referral reason.</summary>
    public Result ReferQuote(string reasonCode, Guid referredBy, DateTimeOffset now) => Refer(reasonCode, referredBy, now);

    private Result Refer(string reasonCode, Guid? referredBy, DateTimeOffset now)
    {
        var result = Fire(QuoteTrigger.ReferQuote);
        if (result.IsSuccess)
        {
            _referrals.Add(UnderwritingReferral.Open(Id, reasonCode, referredBy));
            Raise(new GeneralQuoteReferred(Id, QuoteNumber, QuoteVersion, ProductCode, reasonCode, _risks.Sum(r => r.SumInsured), Currency, now));
        }

        return result;
    }

    /// <summary>
    /// The underwriter's decision on the open referral. Approved returns the quote to Rated (transition "Referral
    /// approved"); declined, with a reason, the quote is Declined for good (transition "Referral declined").
    /// </summary>
    public Result DecideReferral(bool approved, string decisionCode, IReadOnlyList<string> specialConditions, string? declineReason, Guid decidedBy, DateTimeOffset now)
    {
        if (Status != QuoteStatus.Referred || _referrals.SingleOrDefault(r => r.Status == "OPEN") is not { } referral)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Quote {QuoteNumber} v{QuoteVersion} has no open referral.");
        }

        if (!approved && string.IsNullOrWhiteSpace(declineReason))
        {
            return Error.Validation(GeneralErrorCodes.RationaleRequired, "A declined referral records why.");
        }

        // BR-037 (decided by Ade, 28 October 2026): whoever referred a quote does not decide its referral (SRS-002).
        if (referral.ReferredBy == decidedBy)
        {
            return Error.Forbidden(ErrorCodes.MakerCheckerSamePerson, "You referred this quote, so another underwriter decides its referral.");
        }

        referral.Decide(approved, decisionCode, specialConditions, declineReason, decidedBy, now);
        var result = Fire(approved ? QuoteTrigger.ReferralApproved : QuoteTrigger.ReferralDeclined);
        if (result.IsSuccess)
        {
            Raise(new GeneralQuoteReferralDecided(Id, QuoteNumber, QuoteVersion, approved, decisionCode, now));
        }

        if (result.IsSuccess && !approved)
        {
            Raise(new GeneralQuoteDeclined(Id, QuoteNumber, QuoteVersion, decisionCode, declineReason!, now));
        }

        return result;
    }

    /// <summary>Command IssueQuote: valid until the date the product's quote validity period gives. Once issued, the version never changes.</summary>
    public Result IssueQuote(DateOnly validUntil, DateTimeOffset now)
    {
        var result = Fire(QuoteTrigger.IssueQuote);
        if (result.IsSuccess)
        {
            ValidUntil = validUntil;
            Raise(new GeneralQuoteIssued(Id, QuoteNumber, QuoteVersion, ProductCode, ProductVersion, Premium!.Value, Currency, validUntil, now));
        }

        return result;
    }

    /// <summary>Command AcceptQuote: only within its validity period (GEN-001).</summary>
    public Result AcceptQuote(DateOnly today, DateTimeOffset now)
    {
        if (Status == QuoteStatus.Issued && today > ValidUntil)
        {
            return Error.Conflict(GeneralErrorCodes.QuoteExpired, $"Quote {QuoteNumber} v{QuoteVersion} was valid until {ValidUntil:yyyy-MM-dd}.");
        }

        var result = Fire(QuoteTrigger.AcceptQuote);
        if (result.IsSuccess)
        {
            Raise(new GeneralQuoteAccepted(Id, QuoteNumber, QuoteVersion, ProposerPartyId, now));
        }

        return result;
    }

    public Result RejectQuote() => Fire(QuoteTrigger.RejectQuote);

    /// <summary>Command ExpireQuote (transition "Validity ends"): an issued quote past its validity date.</summary>
    public Result ExpireQuote(DateOnly today, DateTimeOffset now)
    {
        if (Status != QuoteStatus.Issued || today <= ValidUntil)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Quote {QuoteNumber} v{QuoteVersion} is not an issued quote past its validity.");
        }

        var result = Fire(QuoteTrigger.ValidityEnds);
        if (result.IsSuccess)
        {
            Raise(new GeneralQuoteExpired(Id, QuoteNumber, QuoteVersion, now));
        }

        return result;
    }

    private Result Fire(QuoteTrigger trigger)
    {
        var next = Lifecycle.Fire(Status, trigger);
        if (next.IsFailure)
        {
            return next.Error;
        }

        Status = next.Value;
        MarkChanged();
        return Result.Success();
    }
}

public enum ProposalStatus
{
    Submitted,
    Accepted,
    Converted,
}

/// <summary>The GeneralProposal aggregate (general.proposal): the proposer's declarations, based on exactly one accepted quote version.</summary>
public sealed class GeneralProposal : AggregateRoot<Guid>
{
    private GeneralProposal()
    {
    }

    public Guid QuoteId { get; private set; }

    public DateTimeOffset SubmittedAt { get; private set; }

    public ProposalStatus Status { get; private set; }

    /// <summary>The declarations as JSON.</summary>
    public string Declarations { get; private set; } = "{}";

    public static Result<GeneralProposal> SubmitProposal(Guid id, GeneralQuote quote, string declarationsJson, DateTimeOffset now)
    {
        if (quote.Status != QuoteStatus.Accepted)
        {
            return Error.Conflict(GeneralErrorCodes.QuoteNotAccepted, $"A proposal is based on an accepted quote; quote {quote.QuoteNumber} v{quote.QuoteVersion} is {quote.Status}.");
        }

        var proposal = new GeneralProposal { Id = Guard.NotEmpty(id), QuoteId = quote.Id, SubmittedAt = now, Declarations = declarationsJson, Status = ProposalStatus.Submitted };
        proposal.MarkChanged();
        return proposal;
    }

    public Result AcceptProposal() => Move(ProposalStatus.Submitted, ProposalStatus.Accepted);

    /// <summary>Transition "Policy issued".</summary>
    public Result Convert() => Status == ProposalStatus.Converted ? Result.Success() : Move(ProposalStatus.Accepted, ProposalStatus.Converted);

    private Result Move(ProposalStatus from, ProposalStatus to)
    {
        if (Status != from)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"The proposal is {Status}, not {from}.");
        }

        Status = to;
        MarkChanged();
        return Result.Success();
    }
}
