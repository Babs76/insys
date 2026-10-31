/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Health.Tests
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
using Insys.Health.Application;
using Insys.Health.Domain.Adjudication;

namespace Insys.Health.Tests;

/// <summary>
/// Worked examples for pharmacy lines (addenda/benefit-programmes.md §4.1, §10). Engineering cases, pending confirmation by whoever runs the benefit:
/// the figures show what the signed defaults do.
/// </summary>
[Trait("Status", "Engineering case, pending actuarial confirmation")]
public sealed class PharmacyWorkedExamples
{
    private static readonly Guid Patient = Guid.CreateVersion7();
    private static readonly DateOnly ServiceDate = new(2026, 6, 15);

    private static readonly FormularyItem Generic = new("NAPPI-1", "METFORMIN", "1", 2m, "MEDICINES", false, 90, 30);
    private static readonly FormularyItem Unpriced = new("NAPPI-2", "INSULIN", "2", null, null, false, null, null);

    private static AdjudicationContext Context(params PrescriptionBenefit[] benefits) => new()
    {
        ClaimId = Guid.CreateVersion7(), ServiceDate = ServiceDate, PatientPartyId = Patient, PrincipalPartyId = Patient, MembershipStart = new DateOnly(2025, 3, 1),
        Plan = new PlanTerms("HLT-CORE", "A", "BS-CORE", "NET-A", "REF", BenefitPeriods.Calendar),
        Provider = new ProviderTerms("PH-1", "PHARMACY", true, null, null),
        Formulary = new Dictionary<string, FormularyItem> { [Generic.ProductCode] = Generic, [Unpriced.ProductCode] = Unpriced },
        PrescriptionBenefits = benefits.Length > 0 ? benefits : [Tier("1"), Tier("2")],
    };

    private static PrescriptionBenefit Tier(string tier, bool covered = true, string? category = "MEDICINES", string? copayType = null, decimal? copay = null, decimal? limit = null,
        int? validity = null, string? plan = "HLT-CORE") => new(plan, tier, covered, category, copayType, copay, limit, validity, null);

    private static ClaimLineInput Line(string product, decimal claimed, int quantity = 30, int? days = 30, DateOnly? prescribed = null, int? repeat = 0, int? repeats = 0) =>
        new(1, product, null, quantity, claimed, [], new PharmacyFacts(days, prescribed, repeat, repeats));

    private static LineResult One(AdjudicationContext context, ClaimLineInput line) => AdjudicationEngine.Adjudicate(context, [line]).Lines.Single();

    [Fact]
    public void ListedMedicine_BelowTheReferencePrice_IsPaidAsClaimed_F044()
    {
        var line = One(Context(), Line("NAPPI-1", 45m)); // reference 30 x 2 = 60
        line.Outcome.ShouldBe(LineOutcome.Pay);
        line.Category.ShouldBe("MEDICINES");
        line.Allowed.ShouldBe(45m);
        line.Paid.ShouldBe(45m);
        line.ActiveIngredient.ShouldBe("METFORMIN");
    }

    [Fact]
    public void AboveTheReferencePrice_TheMemberPaysTheDifference_OQ152()
    {
        var line = One(Context(), Line("NAPPI-1", 100m));
        line.Allowed.ShouldBe(60m);
        line.Paid.ShouldBe(60m);
        line.MemberLiability.ShouldBe(40m);
        line.Reason.ShouldBeNull();
    }

    [Fact]
    public void TheTiersCoPayment_ComesOffWhatIsAllowed_F043()
    {
        var percent = One(Context(Tier("1", copayType: "PERCENT", copay: 20m)), Line("NAPPI-1", 45m));
        percent.CoPayment.ShouldBe(9m);
        percent.Paid.ShouldBe(36m);

        var fixedAmount = One(Context(Tier("1", copayType: "FIXED", copay: 15m)), Line("NAPPI-1", 45m));
        fixedAmount.CoPayment.ShouldBe(15m);
        fixedAmount.Paid.ShouldBe(30m);
    }

    [Fact]
    public void OverTheMaximumQuantity_TheExcessIsNotPaid_AndTheLineSaysWhy_F042()
    {
        var line = One(Context(), Line("NAPPI-1", 240m, quantity: 120, days: 30)); // 90 of 120 payable: claimed share 180, reference 180
        line.PaidQuantity.ShouldBe(90);
        line.Allowed.ShouldBe(180m);
        line.Paid.ShouldBe(180m);
        line.MemberLiability.ShouldBe(60m);
        line.Reason.ShouldBe(AdjudicationReasons.QuantityCut);
    }

    [Fact]
    public void OverTheMaximumDaysSupply_IsCutProRata_F042()
    {
        var line = One(Context(), Line("NAPPI-1", 120m, quantity: 60, days: 60)); // 30 of 60 days payable
        line.PaidQuantity.ShouldBe(30);
        line.Paid.ShouldBe(60m);
        line.Reason.ShouldBe(AdjudicationReasons.QuantityCut);
    }

    [Fact]
    public void NotOnTheFormulary_IsRejected_WithNoRuleForIt_OQ149()
    {
        var line = One(Context(), Line("NAPPI-9", 50m));
        line.Outcome.ShouldBe(LineOutcome.Reject);
        line.Reason.ShouldBe(AdjudicationReasons.NotOnFormulary);
    }

    [Fact]
    public void NotOnTheFormulary_IsPaidAtThePlansCoPayment_WhenItsRuleCoversIt_OQ149()
    {
        var line = One(Context(Tier(FormularyTiers.NonFormulary, category: "MEDICINES", copayType: "PERCENT", copay: 40m)), Line("NAPPI-9", 50m));
        line.Outcome.ShouldBe(LineOutcome.Pay);
        line.CoPayment.ShouldBe(20m);
        line.Paid.ShouldBe(30m);
    }

    [Fact]
    public void ARuleForEveryPlan_GivesWayToThePlansOwn() =>
        One(Context(Tier("1", copayType: "FIXED", copay: 5m, plan: null), Tier("1", copayType: "FIXED", copay: 7m)), Line("NAPPI-1", 45m)).CoPayment.ShouldBe(7m);

    [Fact]
    public void ATierNotCovered_IsRejected_AndATierWithNoRule_PendsForAPerson()
    {
        One(Context(Tier("1", covered: false)), Line("NAPPI-1", 45m)).Reason.ShouldBe(AdjudicationReasons.TierNotCovered);
        var pended = One(Context(Tier("3")), Line("NAPPI-1", 45m));
        pended.Outcome.ShouldBe(LineOutcome.Pend);
        pended.Reason.ShouldBe(AdjudicationReasons.NoPrescriptionBenefit);
    }

    [Fact]
    public void NoReferencePrice_AndTheCategoryComesFromTheBenefit() =>
        One(Context(Tier("2", category: "CHRONIC_RX")), Line("NAPPI-2", 500m)).ShouldSatisfyAllConditions(l => l.Paid.ShouldBe(500m), l => l.Category.ShouldBe("CHRONIC_RX"));

    [Theory]
    [InlineData(2025, 12, 17, LineOutcome.Pay)] // day 180
    [InlineData(2025, 12, 16, LineOutcome.Pend)] // day 181
    [InlineData(2026, 6, 16, LineOutcome.Pend)] // dated after the service
    public void APrescriptionPastItsValidity_Pends_F040(int year, int month, int day, LineOutcome outcome) =>
        One(Context(Tier("1", validity: 180)), Line("NAPPI-1", 45m, prescribed: new DateOnly(year, month, day))).Outcome.ShouldBe(outcome);

    [Fact]
    public void ARepeatBeyondWhatWasPrescribed_Pends_AndAReviewerCanAcceptIt_F040()
    {
        var repeat = Line("NAPPI-1", 45m, repeat: 3, repeats: 2);
        One(Context(), repeat).Reason.ShouldBe(AdjudicationReasons.RepeatExceeded);
        One(Context() with { Reviews = new Dictionary<int, ReviewDecision> { [1] = ReviewDecision.Accept } }, repeat).Outcome.ShouldBe(LineOutcome.Pay);
    }

    [Fact]
    public void ATiersLimit_CountsWhatItsMedicinesWerePaid_AndAReversalGivesItBack()
    {
        var context = Context(Tier("1", limit: 100m)) with
        {
            PriorMedicines = [new PriorMedicine(Guid.CreateVersion7(), "NAPPI-1", "METFORMIN", "1", new DateOnly(2026, 2, 1), 30, 30, 70m)],
        };
        var capped = One(context, Line("NAPPI-1", 45m));
        capped.Paid.ShouldBe(30m);
        capped.Reason.ShouldBe(AdjudicationReasons.Limit);

        One(context with { PriorMedicines = [] }, Line("NAPPI-1", 45m)).Paid.ShouldBe(45m); // the prior claim reversed: it no longer counts
    }

    [Fact]
    public void ATiersLimit_IsSharedByTheLinesOfAClaim_AndLastYearDoesNotCount()
    {
        var context = Context(Tier("1", limit: 60m)) with
        {
            PriorMedicines = [new PriorMedicine(Guid.CreateVersion7(), "NAPPI-1", "METFORMIN", "1", new DateOnly(2025, 12, 31), 30, 30, 500m)],
        };
        var lines = AdjudicationEngine.Adjudicate(context, [Line("NAPPI-1", 45m), Line("NAPPI-1", 45m) with { LineNo = 2 }]).Lines;
        lines[0].Paid.ShouldBe(45m);
        lines[1].Paid.ShouldBe(15m);
    }

    [Fact]
    public void AMedicalLine_IsUnchangedByAFormulary()
    {
        var medical = new ClaimLineInput(1, "NAPPI-1", null, 1, 50m, []);
        var line = One(Context(), medical);
        line.Outcome.ShouldBe(LineOutcome.Pend);
        line.Reason.ShouldBe(AdjudicationReasons.NoBenefitCategory);
        line.PaidQuantity.ShouldBeNull();
    }

    [Theory]
    [InlineData(2008, 10, 31, false)] // 18 today
    [InlineData(2008, 11, 1, true)] // 18 tomorrow
    [InlineData(2008, 2, 29, false)]
    public void AMinor_IsUnderEighteen_OQ161(int year, int month, int day, bool minor) =>
        DependantPrivacy.IsMinor(new DateOnly(year, month, day), new DateOnly(2026, 10, 31)).ShouldBe(minor);

    [Theory]
    [InlineData(null, new string[0], "MEDICAL", null)]
    [InlineData("PHARMACY", new[] { "daysSupply" }, "PHARMACY", null)]
    [InlineData("PHARMACY", new[] { "daysSupply", "prescriber" }, "PHARMACY", "PHARMACY_FIELDS_MISSING")]
    [InlineData("PHARMACY", new string[0], "MEDICAL", "PHARMACY_FIELDS_MISSING")]
    [InlineData(null, new string[0], "PHARMACY", "PHARMACY_LINE_NOT_IN_FORMAT")]
    public void AFormat_SaysWhichLinesItTakes_OQ147(string? kind, string[] required, string line, string? problem) =>
        new ClaimFormatTerms(kind, required).Problem([line == "PHARMACY" ? ["daysSupply"] : null]).ShouldBe(problem);
}
