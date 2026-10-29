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

namespace Insys.General.Domain.Renewals;

/// <summary>A renewal's state (addenda/renewals-and-retention.md §4.2, §4.4).</summary>
public enum RenewalStatus
{
    Reviewing,
    Referred,
    NotEligible,
    Offered,
    Renewed,
    NotRenewed,
}

/// <summary>What a renewal review checked, and its result.</summary>
public static class RenewalChecks
{
    public const string ProductOnSale = "PRODUCT_ON_SALE";
    public const string DueDiligence = "DUE_DILIGENCE";
    public const string FraudReferrals = "FRAUD_REFERRALS";
    public const string ClaimsHistory = "CLAIMS_HISTORY";
    public const string RiskChanges = "RISK_CHANGES";
    public const string EligibilityRules = "ELIGIBILITY_RULES";
    public const string UnderwritingRules = "UNDERWRITING_RULES";
    public const string ReRating = "RE_RATING";

    public const string Pass = "PASS";
    public const string Refer = "REFER";
    public const string Fail = "FAIL";
    public const string Pending = "PENDING";
}

/// <summary>
/// The facts a renewal review starts from (§4.2), gathered by the review: the policy's premium and sums, its claims in the
/// lookback (count, paid, and the premium earned over it), its endorsements this period, claims referred for fraud, whether
/// the product is on sale and the policyholder cleared for issue, the claims experience factor, and what waits on Rules and Rating.
/// </summary>
public sealed record RenewalFacts(decimal CurrentPremium, string Currency, decimal SumInsured, int ClaimsCount, decimal ClaimsPaid, decimal EarnedPremium,
    int Endorsements, int FraudReferredClaims, bool ProductOnSale, bool DueDiligenceCleared, decimal Factor, bool ReRated, string? EligibilityRuleset,
    bool UnderwritingRules)
{
    /// <summary>Claims paid over the premium earned in the lookback; null when nothing was earned.</summary>
    public decimal? LossRatio => EarnedPremium > 0 ? Math.Round(ClaimsPaid / EarnedPremium, 4, MidpointRounding.AwayFromZero) : null;
}

/// <summary>Entity RenewalCheck (general.renewal_check): one check of the review, with its result.</summary>
public sealed class RenewalCheck : Entity<Guid>, ITenantScoped
{
    private RenewalCheck()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid RenewalId { get; private set; }

    public string CheckCode { get; private set; } = "";

    public string Outcome { get; private set; } = "";

    public string? Detail { get; private set; }

    internal static RenewalCheck Create(Guid renewalId, string code, string outcome, string? detail) => new()
    {
        Id = Guid.CreateVersion7(), RenewalId = renewalId, CheckCode = code, Outcome = outcome, Detail = detail is { Length: > 200 } ? detail[..200] : detail,
    };

    internal void Settle(string outcome, string? detail)
    {
        Outcome = outcome;
        Detail = detail is { Length: > 200 } ? detail[..200] : detail;
    }
}

/// <summary>The review asks Rules to evaluate a rule set on its facts (§4.2): ELIGIBILITY with the rule set's code, or UNDERWRITING with the product's.</summary>
public sealed record RenewalRuleEvaluationRequested(Guid RenewalId, string Kind, string Code, IReadOnlyDictionary<string, object?> Inputs, DateTimeOffset OccurredAt)
    : IDomainEvent;

/// <summary>The review re-rates a product that re-rates (F018): a renewal quote is created and priced as new business for the renewal period.</summary>
public sealed record RenewalRatingRequested(Guid RenewalId, Guid PolicyId, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>The subject type a renewal's rule decisions name.</summary>
public static class RenewalSubjects
{
    public const string SubjectType = "GENERAL_RENEWAL";
}

/// <summary>The review found terms to offer: the invitation carries them (§4.2).</summary>
public sealed record GeneralRenewalOffered(Guid RenewalId, Guid PolicyId, string PolicyNumber, decimal CurrentPremium, decimal OfferedPremium, string Currency,
    bool ChangedTerms, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>The review refers the renewal to an underwriter (F019), routed by underwriting authority as a quote referral is.</summary>
public sealed record GeneralRenewalReferred(Guid RenewalId, Guid PolicyId, string PolicyNumber, string ProductCode, decimal SumInsured, string Reason,
    DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>The review found the renewal not eligible (F015): a person proposes non-renewal or overrides it (4.3).</summary>
public sealed record GeneralRenewalNotEligible(Guid RenewalId, Guid PolicyId, string PolicyNumber, string ProductCode, decimal SumInsured, string Reason,
    DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>An underwriter decided a referred or ineligible renewal: offered (with any loading) or not.</summary>
public sealed record GeneralRenewalDecided(Guid RenewalId, Guid PolicyId, string PolicyNumber, bool Offered, string DecisionCode, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// The Renewal aggregate (general.renewal, renewal_check; addenda/renewals-and-retention.md §4.2): the review of a General
/// policy's renewal before its invitation. It checks eligibility (product on sale, due diligence, the eligibility rule set),
/// the changes in the risk and the claims history (referred to the product's underwriting rules), and re-rates a product
/// that re-rates, times the claims experience factor (OQ-131). It ends offered, referred to an underwriter, or not eligible.
/// </summary>
public sealed class Renewal : AggregateRoot<Guid>
{
    private readonly List<RenewalCheck> _checks = [];

    private Renewal()
    {
    }

    public Guid PolicyId { get; private set; }

    public string PolicyNumber { get; private set; } = "";

    public string ProductCode { get; private set; } = "";

    public DateOnly PeriodEnd { get; private set; }

    public RenewalStatus Status { get; private set; }

    public DateOnly OpenedOn { get; private set; }

    public string Currency { get; private set; } = "";

    public decimal CurrentPremium { get; private set; }

    public decimal SumInsured { get; private set; }

    public int ClaimsCount { get; private set; }

    public decimal ClaimsPaid { get; private set; }

    public decimal EarnedPremium { get; private set; }

    public decimal? LossRatio { get; private set; }

    public int Endorsements { get; private set; }

    public int FraudReferredClaims { get; private set; }

    /// <summary>The claims experience factor applied to a re-rated premium (OQ-131); 1.0 when none applies.</summary>
    public decimal Factor { get; private set; }

    public bool ReRated { get; private set; }

    /// <summary>The renewal quote the product was re-rated by.</summary>
    public Guid? QuoteId { get; private set; }

    public decimal? RatedPremium { get; private set; }

    public Guid? EligibilityDecisionId { get; private set; }

    public string? EligibilityOutcome { get; private set; }

    public Guid? UnderwritingDecisionId { get; private set; }

    public string? UnderwritingOutcome { get; private set; }

    public string? ReferralReason { get; private set; }

    /// <summary>An underwriter's loading on the offered premium, in per cent (F019).</summary>
    public decimal? LoadingPct { get; private set; }

    public decimal? OfferedPremium { get; private set; }

    /// <summary>The offered premium differs from the current one (Wave 4 addition 2: invited with the tenant's longer notice).</summary>
    public bool ChangedTerms { get; private set; }

    public string? DecisionCode { get; private set; }

    public Guid? DecidedBy { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    public IReadOnlyList<RenewalCheck> Checks => _checks;

    /// <summary>
    /// Command OpenRenewalReview: the facts are checked now; a rule set and a re-rating wait for Rules and Rating. A product
    /// not on sale or a policyholder not cleared makes it ineligible; claims referred for fraud refer it to a person.
    /// </summary>
    public static Renewal OpenReview(Guid id, Guid policyId, string policyNumber, string productCode, DateOnly periodEnd, RenewalFacts facts, DateOnly today,
        DateTimeOffset now)
    {
        var renewal = new Renewal
        {
            Id = Guard.NotEmpty(id), PolicyId = Guard.NotEmpty(policyId), PolicyNumber = Guard.MaxLength(policyNumber, 30), ProductCode = Guard.MaxLength(productCode, 15),
            PeriodEnd = periodEnd, Status = RenewalStatus.Reviewing, OpenedOn = today, Currency = Guard.MaxLength(facts.Currency, 3), CurrentPremium = facts.CurrentPremium,
            SumInsured = facts.SumInsured, ClaimsCount = facts.ClaimsCount, ClaimsPaid = facts.ClaimsPaid, EarnedPremium = facts.EarnedPremium,
            LossRatio = facts.LossRatio, Endorsements = facts.Endorsements, FraudReferredClaims = facts.FraudReferredClaims, Factor = facts.Factor,
            ReRated = facts.ReRated,
        };
        renewal.Check(RenewalChecks.ProductOnSale, facts.ProductOnSale ? RenewalChecks.Pass : RenewalChecks.Fail,
            facts.ProductOnSale ? null : $"{productCode} is not on sale for the renewal period.");
        renewal.Check(RenewalChecks.DueDiligence, facts.DueDiligenceCleared ? RenewalChecks.Pass : RenewalChecks.Fail,
            facts.DueDiligenceCleared ? null : "The policyholder is not cleared by due diligence for issue.");
        renewal.Check(RenewalChecks.FraudReferrals, facts.FraudReferredClaims == 0 ? RenewalChecks.Pass : RenewalChecks.Refer,
            facts.FraudReferredClaims == 0 ? null : $"{facts.FraudReferredClaims} claim(s) referred for fraud.");
        renewal.Check(RenewalChecks.ClaimsHistory, RenewalChecks.Pass,
            $"{facts.ClaimsCount} claim(s), {facts.ClaimsPaid:0.00} {facts.Currency} paid, loss ratio {(facts.LossRatio is { } ratio ? ratio.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture) : "n/a")}.");
        renewal.Check(RenewalChecks.RiskChanges, RenewalChecks.Pass, $"{facts.Endorsements} endorsement(s) this period.");
        if (facts.EligibilityRuleset is not null)
        {
            renewal.Check(RenewalChecks.EligibilityRules, RenewalChecks.Pending, facts.EligibilityRuleset);
        }

        if (facts.UnderwritingRules)
        {
            renewal.Check(RenewalChecks.UnderwritingRules, RenewalChecks.Pending, null);
        }

        if (facts.ReRated)
        {
            renewal.Check(RenewalChecks.ReRating, RenewalChecks.Pending, null);
        }

        renewal.MarkChanged();
        if (facts.EligibilityRuleset is { } ruleset)
        {
            renewal.Raise(new RenewalRuleEvaluationRequested(renewal.Id, "ELIGIBILITY", ruleset, RuleInputs(facts), now));
        }

        if (facts.UnderwritingRules)
        {
            renewal.Raise(new RenewalRuleEvaluationRequested(renewal.Id, "UNDERWRITING", productCode, RuleInputs(facts), now));
        }

        if (facts.ReRated)
        {
            renewal.Raise(new RenewalRatingRequested(renewal.Id, policyId, now));
        }

        renewal.Conclude(now);
        return renewal;
    }

    /// <summary>The facts a renewal's rule sets read (Rules): business figures only, never personal data (INT-007).</summary>
    public static IReadOnlyDictionary<string, object?> RuleInputs(RenewalFacts facts) => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["renewal"] = true,
        ["claims_count"] = facts.ClaimsCount,
        ["claims_paid"] = facts.ClaimsPaid,
        ["loss_ratio"] = facts.LossRatio,
        ["endorsements"] = facts.Endorsements,
        ["fraud_referred_claims"] = facts.FraudReferredClaims,
        ["sum_insured"] = facts.SumInsured,
        ["premium"] = facts.CurrentPremium,
    };

    /// <summary>Rules' decision on the eligibility rule set: INELIGIBLE makes the renewal ineligible, REFER refers it (F015).</summary>
    public Result RecordEligibility(Guid decisionId, string outcome, DateTimeOffset now)
    {
        if (EligibilityDecisionId == decisionId)
        {
            return Result.Success();
        }

        if (Status != RenewalStatus.Reviewing || Pending(RenewalChecks.EligibilityRules) is not { } check)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Renewal of {PolicyNumber} is not waiting for an eligibility decision.");
        }

        EligibilityDecisionId = decisionId;
        EligibilityOutcome = Guard.MaxLength(outcome, 15);
        check.Settle(outcome switch { "INELIGIBLE" => RenewalChecks.Fail, "REFER" => RenewalChecks.Refer, _ => RenewalChecks.Pass }, $"Rule set outcome {outcome}.");
        MarkChanged();
        Conclude(now);
        return Result.Success();
    }

    /// <summary>Rules' decision on the product's underwriting rules: DECLINE makes it ineligible, REFER refers it (F016, F017, F019).</summary>
    public Result RecordUnderwriting(Guid decisionId, string outcome, IReadOnlyList<string> firedRules, DateTimeOffset now)
    {
        if (UnderwritingDecisionId == decisionId)
        {
            return Result.Success();
        }

        if (Status != RenewalStatus.Reviewing || Pending(RenewalChecks.UnderwritingRules) is not { } check)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Renewal of {PolicyNumber} is not waiting for an underwriting decision.");
        }

        UnderwritingDecisionId = decisionId;
        UnderwritingOutcome = Guard.MaxLength(outcome, 15);
        check.Settle(outcome switch { "DECLINE" => RenewalChecks.Fail, "REFER" => RenewalChecks.Refer, _ => RenewalChecks.Pass },
            firedRules.Count == 0 ? $"Outcome {outcome}." : $"Outcome {outcome}: {string.Join(", ", firedRules)}.");
        MarkChanged();
        Conclude(now);
        return Result.Success();
    }

    /// <summary>The renewal quote was priced (F018): the offered premium is it times the claims experience factor.</summary>
    public Result RecordRating(Guid quoteId, decimal premium, DateTimeOffset now)
    {
        if (QuoteId == quoteId && RatedPremium is not null)
        {
            return Result.Success();
        }

        if (Status != RenewalStatus.Reviewing || Pending(RenewalChecks.ReRating) is not { } check)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Renewal of {PolicyNumber} is not waiting for a rating.");
        }

        QuoteId = quoteId;
        RatedPremium = premium;
        check.Settle(RenewalChecks.Pass, $"Re-rated at {premium:0.00} {Currency}, factor {Factor:0.0000}.");
        MarkChanged();
        Conclude(now);
        return Result.Success();
    }

    /// <summary>The renewal quote could not be priced: the renewal is referred for an underwriter to set its terms.</summary>
    public Result RecordRatingIssue(Guid quoteId, string reason, DateTimeOffset now)
    {
        if (Status != RenewalStatus.Reviewing || Pending(RenewalChecks.ReRating) is not { } check)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Renewal of {PolicyNumber} is not waiting for a rating.");
        }

        QuoteId = quoteId;
        check.Settle(RenewalChecks.Refer, reason);
        MarkChanged();
        Conclude(now);
        return Result.Success();
    }

    /// <summary>The renewal quote this review created, linked before it is priced.</summary>
    public void LinkQuote(Guid quoteId)
    {
        QuoteId ??= quoteId;
        MarkChanged();
    }

    /// <summary>
    /// Command DecideRenewalReferral (F019), within the underwriter's authority: accepted, the renewal is offered at the
    /// reviewed premium with any loading; declined, it stays not eligible for non-renewal (4.3).
    /// </summary>
    public Result DecideReferral(bool accept, string decisionCode, decimal? loadingPct, Guid by, DateTimeOffset now)
    {
        if (Status is not (RenewalStatus.Referred or RenewalStatus.NotEligible))
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Renewal of {PolicyNumber} is {Status}; only a referred or ineligible renewal is decided.");
        }

        if (loadingPct is < 0 or > 500)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "A loading is between 0 and 500 per cent.");
        }

        DecisionCode = Guard.MaxLength(decisionCode, 15);
        DecidedBy = by;
        DecidedAt = now;
        if (accept)
        {
            LoadingPct = loadingPct;
            Offer(now);
        }
        else if (Status == RenewalStatus.Referred)
        {
            Status = RenewalStatus.NotEligible;
            ReferralReason ??= "Declined by the underwriter.";
        }

        MarkChanged();
        Raise(new GeneralRenewalDecided(Id, PolicyId, PolicyNumber, accept, DecisionCode, now));
        return Result.Success();
    }

    /// <summary>The policy was renewed on the offered terms.</summary>
    public Result MarkRenewed()
    {
        if (Status != RenewalStatus.Offered)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Renewal of {PolicyNumber} is {Status}.");
        }

        Status = RenewalStatus.Renewed;
        MarkChanged();
        return Result.Success();
    }

    /// <summary>The premium the review would offer: the re-rated premium times the factor when re-rated, else the current one; then any loading.</summary>
    private decimal Terms()
    {
        var basis = ReRated && RatedPremium is { } rated ? rated * Factor : CurrentPremium;
        var loaded = LoadingPct is { } loading ? basis * (1 + loading / 100m) : basis;
        return Math.Round(loaded, 2, MidpointRounding.AwayFromZero);
    }

    private void Conclude(DateTimeOffset now)
    {
        if (Status != RenewalStatus.Reviewing)
        {
            return;
        }

        if (_checks.FirstOrDefault(c => c.Outcome == RenewalChecks.Fail) is { } failed)
        {
            Status = RenewalStatus.NotEligible;
            ReferralReason = failed.Detail ?? failed.CheckCode;
            Raise(new GeneralRenewalNotEligible(Id, PolicyId, PolicyNumber, ProductCode, SumInsured, ReferralReason, now));
            return;
        }

        if (_checks.Any(c => c.Outcome == RenewalChecks.Pending))
        {
            return;
        }

        if (_checks.FirstOrDefault(c => c.Outcome == RenewalChecks.Refer) is { } referred)
        {
            Status = RenewalStatus.Referred;
            ReferralReason = referred.Detail ?? referred.CheckCode;
            Raise(new GeneralRenewalReferred(Id, PolicyId, PolicyNumber, ProductCode, SumInsured, ReferralReason, now));
            return;
        }

        Offer(now);
    }

    private void Offer(DateTimeOffset now)
    {
        OfferedPremium = Terms();
        ChangedTerms = OfferedPremium != CurrentPremium;
        Status = RenewalStatus.Offered;
        Raise(new GeneralRenewalOffered(Id, PolicyId, PolicyNumber, CurrentPremium, OfferedPremium.Value, Currency, ChangedTerms, now));
    }

    private RenewalCheck? Pending(string code) => _checks.FirstOrDefault(c => c.CheckCode == code && c.Outcome == RenewalChecks.Pending);

    private void Check(string code, string outcome, string? detail) => _checks.Add(RenewalCheck.Create(Id, code, outcome, detail));
}
