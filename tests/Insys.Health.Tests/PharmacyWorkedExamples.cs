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
        var lines = AdjudicationEngine.Adjudicate(context, [Line("NAPPI-1", 45m, days: null), Line("NAPPI-1", 45m, days: null) with { LineNo = 2 }]).Lines;
        lines[0].Paid.ShouldBe(45m);
        lines[1].Paid.ShouldBe(15m);
    }

    private static PriorMedicine Taken(DateOnly on, int quantity = 30, int days = 30, decimal paid = 45m, string ingredient = "METFORMIN", string product = "NAPPI-1") =>
        new(Guid.CreateVersion7(), product, ingredient, "1", on, quantity, days, paid);

    [Theory]
    [InlineData(2026, 5, 19, LineOutcome.Pay)] // 27 days on, of 30 with 10%: the first day allowed
    [InlineData(2026, 5, 20, LineOutcome.Pend)]
    [InlineData(2026, 6, 15, LineOutcome.Pend)] // a different product, the same ingredient, the same day
    public void TheSameIngredient_BeforeTheSupplyHasRunOut_Pends_F047_OQ151(int year, int month, int day, LineOutcome outcome)
    {
        var context = Context() with { PriorMedicines = [Taken(new DateOnly(year, month, day), product: "NAPPI-7")] };
        var line = One(context, Line("NAPPI-1", 45m));
        line.Outcome.ShouldBe(outcome);
        if (outcome == LineOutcome.Pend)
        {
            line.Reason.ShouldBe(AdjudicationReasons.DuplicateTherapy);
        }
    }

    [Fact]
    public void AnEarlyRefill_AcrossTheEndOfFebruary_CountsCalendarDays()
    {
        var context = Context() with { ServiceDate = new DateOnly(2028, 3, 27), PriorMedicines = [Taken(new DateOnly(2028, 2, 29))] }; // a leap year: 27 days on
        One(context, Line("NAPPI-1", 45m)).Outcome.ShouldBe(LineOutcome.Pay);
        One(context with { ServiceDate = new DateOnly(2028, 3, 26) }, Line("NAPPI-1", 45m)).Reason.ShouldBe(AdjudicationReasons.DuplicateTherapy);
    }

    [Fact]
    public void ThePlansRefillTolerance_ReplacesTheDefault_OQ151()
    {
        var benefit = Tier("1") with { RefillTolerancePercent = 0m };
        One(Context(benefit) with { PriorMedicines = [Taken(new DateOnly(2026, 5, 19))] }, Line("NAPPI-1", 45m)).Reason.ShouldBe(AdjudicationReasons.DuplicateTherapy);
        One(Context(benefit) with { PriorMedicines = [Taken(new DateOnly(2026, 5, 16))] }, Line("NAPPI-1", 45m)).Outcome.ShouldBe(LineOutcome.Pay); // 30 days on
    }

    [Fact]
    public void AnIngredientsQuantityLimit_CutsTheLine_AcrossProducts_F046()
    {
        var context = Context() with
        {
            MedicationLimits = [new MedicationLimit(null, null, "METFORMIN", 40, null, "P1M")],
            PriorMedicines = [Taken(new DateOnly(2026, 6, 1), quantity: 20, days: 1, product: "NAPPI-7")],
        };
        var line = One(context, Line("NAPPI-1", 45m));
        line.PaidQuantity.ShouldBe(20);
        line.Paid.ShouldBe(30m);
        line.Reason.ShouldBe(AdjudicationReasons.QuantityCut);
    }

    [Theory]
    [InlineData(2026, 5, 15, 45)] // a month before: its period has just ended, so nothing is counted
    [InlineData(2026, 5, 16, 15)] // within the month: 30 of 45 is used
    public void AProductsAmountLimit_CountsWithinItsPeriod_F046(int year, int month, int day, int paid)
    {
        var context = Context() with
        {
            MedicationLimits = [new MedicationLimit(null, "NAPPI-1", null, null, 45m, "P1M")],
            PriorMedicines = [Taken(new DateOnly(year, month, day), days: 1, paid: 30m)],
        };
        One(context, Line("NAPPI-1", 45m)).Paid.ShouldBe((decimal)paid);
    }

    [Fact]
    public void AMedicationLimit_ForAnotherPlan_DoesNotApply() =>
        One(Context() with { MedicationLimits = [new MedicationLimit("OTHER", "NAPPI-1", null, null, 1m, "P1Y")] }, Line("NAPPI-1", 45m)).Paid.ShouldBe(45m);

    [Fact]
    public void AMedicationLimit_OnTopOfACategoryLimit_TheMostRestrictiveWins_F046()
    {
        var context = Context() with { ScheduleLimits = [new ScheduleLimit("MEDICINES", 40m, "PER_EVENT")], MedicationLimits = [new MedicationLimit(null, "NAPPI-1", null, null, 25m, "P1Y")] };
        One(context, Line("NAPPI-1", 45m)).Paid.ShouldBe(25m);
        One(context with { MedicationLimits = [new MedicationLimit(null, "NAPPI-1", null, null, 44m, "P1Y")] }, Line("NAPPI-1", 45m)).Paid.ShouldBe(40m);
    }

    [Fact]
    public void AMedicineNeedingAuthorisation_PendsWithoutOne_AndIsPaidWithOne_F093()
    {
        var restricted = Generic with { ProductCode = "NAPPI-R", PriorAuthRequired = true };
        var context = Context() with { Formulary = new Dictionary<string, FormularyItem> { ["NAPPI-R"] = restricted } };
        One(context, Line("NAPPI-R", 45m)).Reason.ShouldBe(AdjudicationReasons.PreauthRequired);
        var authorised = context with { Authorised = [new AuthorisedLine(Guid.CreateVersion7(), Guid.CreateVersion7(), "NAPPI-R", 100m, 100m, 0m, "MEDICINES")] };
        One(authorised, Line("NAPPI-R", 45m)).Outcome.ShouldBe(LineOutcome.Pay);
    }

    private static readonly ChronicBenefit Diabetes = new("CHRONIC", ["E10-E14"], [new ChronicEntitlement(null, "METFORMIN", 60)]);

    [Fact]
    public void AnEntitledMedicine_DrawsOnTheChronicCategory_F084()
    {
        var line = One(Context() with { ChronicBenefits = [Diabetes] }, Line("NAPPI-1", 45m));
        line.Category.ShouldBe("CHRONIC");
        line.Paid.ShouldBe(45m);
    }

    [Fact]
    public void AboveTheMonthlyEntitlement_TheExcessIsNotPaid_AndLastMonthDoesNotCount_F085()
    {
        var taken = new PriorMedicine(Guid.CreateVersion7(), "NAPPI-7", "METFORMIN", "1", new DateOnly(2026, 6, 1), 40, null, 40m);
        var line = One(Context() with { ChronicBenefits = [Diabetes], PriorMedicines = [taken] }, Line("NAPPI-1", 45m));
        line.PaidQuantity.ShouldBe(20);
        line.Reason.ShouldBe(AdjudicationReasons.QuantityCut);

        var lastMonth = taken with { ServiceDate = new DateOnly(2026, 5, 31) };
        One(Context() with { ChronicBenefits = [Diabetes], PriorMedicines = [lastMonth] }, Line("NAPPI-1", 45m)).PaidQuantity.ShouldBe(30);
    }

    [Fact]
    public void AMedicalLine_WithARegisteredDiagnosis_DrawsOnTheChronicCategory_AndOtherDiagnosesDoNot_F084()
    {
        var context = Context() with
        {
            ChronicBenefits = [Diabetes], Tariffs = new Dictionary<(string, string), TariffPrice> { [("REF", "0190")] = new(100m, null) }, CodeSystems = new Dictionary<string, string> { ["0190"] = "NRPL" },
            Mappings = [new("NRPL", "0190", "0199", null, "GP")], Provider = new ProviderTerms("PH-1", "GP", true, null, null),
        };
        One(context, new ClaimLineInput(1, "0190", "E11", 1, 80m, [])).Category.ShouldBe("CHRONIC");
        One(context, new ClaimLineInput(1, "0190", "K35", 1, 80m, [])).Category.ShouldBe("GP");
        One(context, new ClaimLineInput(1, "0190", null, 1, 80m, [])).Category.ShouldBe("GP");
    }

    private static AdjudicationContext Preventive(DateOnly? born, string? sex = "F", params PriorService[] history) => Context() with
    {
        PreventiveRules = [new PreventiveRule(null, "FLU", "PREVENTIVE", 65, null, "F", "P1Y", true)], PreventiveHistory = history, PatientBirthDate = born, PatientSex = sex,
        Tariffs = new Dictionary<(string, string), TariffPrice> { [("REF", "FLU")] = new(100m, null) }, CodeSystems = new Dictionary<string, string> { ["FLU"] = "NRPL" },
        Mappings = [new(null, null, null, "GP", "DAY_TO_DAY")], Provider = new ProviderTerms("PH-1", "GP", true, null, null),
        CostShares = [new CostShareRule("CO_PAYMENT", "PREVENTIVE", "PERCENT", 20m, "IN_NETWORK", null, "PER_LINE", null), new CostShareRule("CO_PAYMENT", "DAY_TO_DAY", "PERCENT", 20m, "IN_NETWORK", null, "PER_LINE", null)],
    };

    private static readonly ClaimLineInput Flu = new(1, "FLU", null, 1, 100m, []);

    [Theory]
    [InlineData(1961, 6, 15, "F", "PREVENTIVE", 0)] // 65 today: within schedule, no co-payment
    [InlineData(1961, 6, 16, "F", "DAY_TO_DAY", 20)] // 65 tomorrow: outside the age band
    [InlineData(1950, 1, 1, "M", "DAY_TO_DAY", 20)] // the wrong sex
    public void AScheduledItem_IsCheckedAgainstAgeAndSex_F087_F088(int year, int month, int day, string sex, string category, int copay)
    {
        var line = One(Preventive(new DateOnly(year, month, day), sex), Flu);
        (line.Category, line.CoPayment).ShouldBe((category, (decimal)copay));
    }

    [Fact]
    public void AScheduledItem_UsedWithinItsInterval_IsDayToDay_AndAfterItIsPreventive_F087()
    {
        var born = new DateOnly(1950, 1, 1);
        PriorService Used(DateOnly on) => new(Guid.CreateVersion7(), "FLU", "PREVENTIVE", on, "PH-1");
        One(Preventive(born, "F", Used(new DateOnly(2025, 6, 16))), Flu).Category.ShouldBe("DAY_TO_DAY");
        One(Preventive(born, "F", Used(new DateOnly(2025, 6, 15))), Flu).Category.ShouldBe("PREVENTIVE");
    }

    [Fact]
    public void AnUnknownBirthDate_IsNotWithinAnAgeBand() => One(Preventive(null), Flu).Category.ShouldBe("DAY_TO_DAY");

    [Fact]
    public void AnEnrolledProgramme_GivesItsConditionsClaims_ItsCategory_F086()
    {
        var context = Preventive(null) with { ProgrammeBenefits = [new ProgrammeBenefit("PROGRAMME", ["E11"])] };
        One(context, Flu with { DiagnosisCode = "E11" }).Category.ShouldBe("PROGRAMME");
        One(context, Flu with { DiagnosisCode = "K35" }).Category.ShouldBe("DAY_TO_DAY");
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
