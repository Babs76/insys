/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : General.Tests
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
using Insys.General.Domain.Renewals;

namespace Insys.General.Tests;

/// <summary>The renewal review (addenda/renewals-and-retention.md §4.2; OQ-131; Wave 4 addition 2).</summary>
public sealed class RenewalTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 21);
    private static readonly DateOnly PeriodEnd = new(2026, 12, 31);

    private static RenewalFacts Facts(bool reRated = false, decimal factor = 1m, bool onSale = true, bool cleared = true, int fraud = 0, string? eligibility = null,
        bool underwriting = false) =>
        new(3_050m, "ZAR", 100_000m, 2, 1_500m, 3_000m, 1, fraud, onSale, cleared, factor, reRated, eligibility, underwriting);

    private static Renewal Open(RenewalFacts facts) => Renewal.OpenReview(Guid.CreateVersion7(), Guid.CreateVersion7(), "GP0001", "GMOT", PeriodEnd, facts, Today, Now);

    [Fact]
    public void AReviewWithNothingToWaitFor_OffersTheCurrentPremium_UnchangedTerms()
    {
        var renewal = Open(Facts());
        (renewal.Status, renewal.OfferedPremium, renewal.ChangedTerms, renewal.LossRatio).ShouldBe((RenewalStatus.Offered, (decimal?)3_050m, false, (decimal?)0.5m));
        renewal.Checks.Select(c => c.CheckCode).ShouldBe([RenewalChecks.ProductOnSale, RenewalChecks.DueDiligence, RenewalChecks.FraudReferrals, RenewalChecks.ClaimsHistory,
            RenewalChecks.RiskChanges]);
        renewal.DomainEvents.OfType<GeneralRenewalOffered>().Single().ChangedTerms.ShouldBeFalse();
    }

    /// <summary>F018, OQ-131: a re-rated renewal waits for its rating; the offer is the rated premium times the claims experience factor, changed terms.</summary>
    [Fact]
    public void AReRatedRenewal_OffersTheRatedPremiumTimesTheFactor_F018_OQ131()
    {
        var renewal = Open(Facts(reRated: true, factor: 1.15m));
        renewal.Status.ShouldBe(RenewalStatus.Reviewing);
        renewal.DomainEvents.OfType<RenewalRatingRequested>().Count().ShouldBe(1);
        var quote = Guid.CreateVersion7();
        renewal.RecordRating(quote, 3_200m, Now).IsSuccess.ShouldBeTrue();
        (renewal.Status, renewal.QuoteId, renewal.RatedPremium, renewal.OfferedPremium, renewal.ChangedTerms)
            .ShouldBe((RenewalStatus.Offered, (Guid?)quote, (decimal?)3_200m, (decimal?)3_680m, true));
        renewal.RecordRating(quote, 3_200m, Now).IsSuccess.ShouldBeTrue("a redelivery changes nothing");
    }

    /// <summary>F019: the product's underwriting rules refer it; an underwriter offers it with a loading, or declines it.</summary>
    [Fact]
    public void AReferredRenewal_IsOfferedWithTheUnderwritersLoading_OrDeclined_F019()
    {
        var renewal = Open(Facts(underwriting: true));
        renewal.DomainEvents.OfType<RenewalRuleEvaluationRequested>().Single().ShouldSatisfyAllConditions(e => e.Kind.ShouldBe("UNDERWRITING"), e => e.Code.ShouldBe("GMOT"),
            e => e.Inputs.Keys.ShouldBe(["renewal", "claims_count", "claims_paid", "loss_ratio", "endorsements", "fraud_referred_claims", "sum_insured", "premium"]));
        renewal.RecordUnderwriting(Guid.CreateVersion7(), "REFER", ["HIGH_LOSS_RATIO"], Now).IsSuccess.ShouldBeTrue();
        (renewal.Status, renewal.ReferralReason).ShouldBe((RenewalStatus.Referred, "Outcome REFER: HIGH_LOSS_RATIO."));
        renewal.DomainEvents.OfType<GeneralRenewalReferred>().Single().SumInsured.ShouldBe(100_000m);

        renewal.DecideReferral(true, "ACCEPT_LOADED", 10m, Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        (renewal.Status, renewal.OfferedPremium, renewal.ChangedTerms, renewal.LoadingPct).ShouldBe((RenewalStatus.Offered, (decimal?)3_355m, true, (decimal?)10m));

        var declined = Open(Facts(underwriting: true));
        declined.RecordUnderwriting(Guid.CreateVersion7(), "REFER", [], Now);
        declined.DecideReferral(false, "DECLINE", null, Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        declined.Status.ShouldBe(RenewalStatus.NotEligible);
        declined.DomainEvents.OfType<GeneralRenewalDecided>().Single().Offered.ShouldBeFalse();
    }

    /// <summary>F015: a product off sale, a policyholder not cleared, or an INELIGIBLE rule set make it ineligible; fraud referrals refer it to a person.</summary>
    [Fact]
    public void Eligibility_FailsOnSaleClearanceOrRules_AndFraudReferralsReferIt_F015()
    {
        Open(Facts(onSale: false)).Status.ShouldBe(RenewalStatus.NotEligible);
        var notCleared = Open(Facts(cleared: false));
        (notCleared.Status, notCleared.ReferralReason).ShouldBe((RenewalStatus.NotEligible, "The policyholder is not cleared by due diligence for issue."));
        notCleared.DomainEvents.OfType<GeneralRenewalNotEligible>().Count().ShouldBe(1);

        var rules = Open(Facts(eligibility: "RS-REN"));
        rules.Status.ShouldBe(RenewalStatus.Reviewing);
        rules.RecordEligibility(Guid.CreateVersion7(), "INELIGIBLE", Now).IsSuccess.ShouldBeTrue();
        rules.Status.ShouldBe(RenewalStatus.NotEligible);

        Open(Facts(fraud: 1)).Status.ShouldBe(RenewalStatus.Referred);
        var both = Open(Facts(fraud: 1, eligibility: "RS-REN"));
        both.Status.ShouldBe(RenewalStatus.Reviewing, "a referral waits for the other checks");
        both.RecordEligibility(Guid.CreateVersion7(), "ELIGIBLE", Now);
        both.Status.ShouldBe(RenewalStatus.Referred);
    }

    /// <summary>A renewal quote Rating could not price refers the renewal for an underwriter to set its terms.</summary>
    [Fact]
    public void ARatingIssue_RefersTheRenewal()
    {
        var renewal = Open(Facts(reRated: true));
        renewal.RecordRatingIssue(Guid.CreateVersion7(), "Rating could not price it.", Now).IsSuccess.ShouldBeTrue();
        (renewal.Status, renewal.ReferralReason).ShouldBe((RenewalStatus.Referred, "Rating could not price it."));
        renewal.DecideReferral(true, "ACCEPT", null, Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        renewal.OfferedPremium.ShouldBe(3_050m, "no rating: the current premium, as the underwriter accepts it");
    }
}
