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
using Insys.BuildingBlocks.Domain.Expressions;
using Insys.Health.Domain.Adjudication;

namespace Insys.Health.Tests;

/// <summary>
/// Worked examples for each step of ADR-012 (a to h). Engineering cases, pending actuarial confirmation: the figures
/// show what the proposed defaults do, not agreed business outcomes.
/// </summary>
[Trait("Status", "Engineering case, pending actuarial confirmation")]
public sealed class AdjudicationWorkedExamples
{
    private static readonly Guid Principal = Guid.CreateVersion7();
    private static readonly Guid Spouse = Guid.CreateVersion7();
    private static readonly DateOnly MemberSince = new(2025, 3, 1);
    private static readonly DateOnly ServiceDate = new(2026, 6, 15);

    private static AdjudicationContext Context(Guid? patient = null, bool inNetwork = true, string? contract = "CON-1", string? treatment = null) => new()
    {
        ClaimId = Guid.CreateVersion7(),
        ServiceDate = ServiceDate,
        PatientPartyId = patient ?? Principal,
        PrincipalPartyId = Principal,
        MembershipStart = MemberSince,
        Plan = new PlanTerms("HLT-CORE", "A", "BS-CORE", "NET-A", "REF", BenefitPeriods.Calendar),
        Provider = new ProviderTerms("PR-1", "GP", inNetwork, inNetwork ? contract : null, inNetwork ? "NET-1" : null),
        OutOfNetworkTreatment = treatment,
        Tariffs = new Dictionary<(string, string), TariffPrice>
        {
            [("CON-1", "0190")] = new(500m, null),
            [("NET-1", "0190")] = new(450m, null),
            [("REF", "0190")] = new(400m, null),
            [("NET-1", "0191")] = new(300m, ["AFTER_HOURS"]),
            [("REF", "0195")] = new(200m, null),
            [("REF", "0300")] = new(1_000m, null),
            [("REF", "7777")] = new(100m, null),
        },
        CodeSystems = new Dictionary<string, string> { ["0190"] = "NRPL", ["0191"] = "NRPL", ["0195"] = "NRPL", ["0300"] = "NRPL", ["7777"] = "LOCAL" },
        Mappings =
        [
            new("NRPL", "0190", "0199", null, "GP_VISITS"),
            new("NRPL", "0190", "0190", null, "GP_CONSULT"),
            new("NRPL", "0300", "0399", null, "SPECIALIST"),
            new(null, null, null, "GP", "GENERAL"),
        ],
    };

    private static ClaimLineInput Line(string code, decimal claimed, int quantity = 1, string? diagnosis = null, params string[] modifiers) =>
        new(1, code, diagnosis, quantity, claimed, modifiers);

    private static LineResult One(AdjudicationContext context, ClaimLineInput line) => AdjudicationEngine.Adjudicate(context, [line]).Lines.Single();

    // (a) Benefit category mapping.

    [Fact]
    public void A_AnExactCodeMapping_WinsOverTheRangeThatAlsoCoversIt()
    {
        One(Context(), Line("0190", 400m)).Category.ShouldBe("GP_CONSULT");
        One(Context(), Line("0195", 150m)).Category.ShouldBe("GP_VISITS");
    }

    [Fact]
    public void A_ACodeNoMappingCovers_FallsBackToTheProviderType_ElseThePends()
    {
        One(Context(), Line("7777", 50m)).Category.ShouldBe("GENERAL");

        var noFallback = Context() with { Provider = new ProviderTerms("PR-9", "SPECIALIST", true, null, null) };
        var line = One(noFallback, Line("7777", 50m));
        line.Outcome.ShouldBe(LineOutcome.Pend);
        line.Reason.ShouldBe(AdjudicationReasons.NoBenefitCategory);
    }

    // (b) Tariff resolution.

    [Fact]
    public void B_InNetwork_TheContractTariffComesFirst_ThenTheNetwork_ThenTheReference()
    {
        One(Context(), Line("0190", 900m)).Tariff.ShouldBe(500m);
        One(Context(contract: null), Line("0190", 900m)).Tariff.ShouldBe(450m);
        One(Context(contract: null), Line("0195", 900m)).Tariff.ShouldBe(200m);
    }

    [Fact]
    public void B_OutOfNetwork_TheReferenceTariffApplies_AndNoPriceAnywherePends()
    {
        One(Context(inNetwork: false, treatment: "COVERED_AT_TARIFF"), Line("0190", 900m)).Tariff.ShouldBe(400m);

        var noTariff = One(Context(inNetwork: false, treatment: "COVERED_AT_TARIFF"), Line("0191", 900m));
        noTariff.Outcome.ShouldBe(LineOutcome.Pend);
        noTariff.Reason.ShouldBe(AdjudicationReasons.NoTariff);
    }

    // (c) Tariff modifiers.

    [Fact]
    public void C_Modifiers_ApplyInSortOrder_PercentUpliftFixedAddAndMultiplier()
    {
        var context = Context(contract: null) with
        {
            Tariffs = new Dictionary<(string, string), TariffPrice> { [("NET-1", "0190")] = new(400m, null) },
            Modifiers =
            [
                new("AFTER_HOURS", "PERCENT_UPLIFT", 25m, null, 1),
                new("CALL_OUT", "FIXED_ADD", 50m, ["0190-0199"], 2),
                new("ASSISTANT", "MULTIPLIER", 0.5m, null, 3),
            ],
        };

        // 400 × 1.25 = 500; + 50 = 550; × 0.5 = 275; two units = 550.
        One(context, Line("0190", 2_000m, 2, null, "ASSISTANT", "AFTER_HOURS", "CALL_OUT")).Tariff.ShouldBe(550m);
    }

    [Fact]
    public void C_AModifierTheTariffDoesNotAllow_OrThatIsNotInForce_Pends()
    {
        var context = Context(contract: null) with { Modifiers = [new("AFTER_HOURS", "PERCENT_UPLIFT", 25m, null, 1), new("WEEKEND", "FIXED_ADD", 10m, null, 2)] };

        One(context, Line("0191", 1_000m, 1, null, "AFTER_HOURS")).Tariff.ShouldBe(375m);
        One(context, Line("0191", 1_000m, 1, null, "WEEKEND")).Reason.ShouldBe(AdjudicationReasons.ModifierNotAllowed);
        One(context, Line("0191", 1_000m, 1, null, "NIGHT")).Reason.ShouldBe(AdjudicationReasons.ModifierNotAllowed);
    }

    // (d) Allowed amount.

    [Fact]
    public void D_InNetwork_TheAllowedAmountIsTheLesserOfClaimedAndTariff()
    {
        One(Context(), Line("0190", 650m)).Allowed.ShouldBe(500m);
        One(Context(), Line("0190", 420m)).Allowed.ShouldBe(420m);
    }

    [Fact]
    public void D_OutOfNetwork_FollowsThePlansRestrictionRule()
    {
        var notCovered = One(Context(inNetwork: false, treatment: "NOT_COVERED"), Line("0190", 650m));
        notCovered.Outcome.ShouldBe(LineOutcome.Reject);
        notCovered.Reason.ShouldBe(AdjudicationReasons.OutOfNetwork);
        notCovered.MemberLiability.ShouldBe(650m);

        One(Context(inNetwork: false, treatment: null), Line("0190", 650m)).Reason.ShouldBe(AdjudicationReasons.NoNetworkRule);

        // CO_PAYMENT: the reference tariff, and the out-of-network co-payment rule (20%), not the in-network one (10%).
        var context = Context(inNetwork: false, treatment: "CO_PAYMENT") with
        {
            CostShares =
            [
                new("CO_PAYMENT", "GP_CONSULT", "PERCENT", 10m, "IN_NETWORK", null, "PER_LINE", null),
                new("CO_PAYMENT", "GP_CONSULT", "PERCENT", 20m, "OUT_OF_NETWORK", null, "PER_LINE", null),
            ],
        };
        var coPayment = One(context, Line("0190", 650m));
        coPayment.Allowed.ShouldBe(400m);
        coPayment.CoPayment.ShouldBe(80m);
        coPayment.Paid.ShouldBe(320m);
        coPayment.MemberLiability.ShouldBe(330m);
    }

    // (e) Order: allowed, deductible, co-payment with its cap, then limits.

    [Fact]
    public void E_DeductibleThenCappedCoPaymentThenTheLimit_AndTheMemberPaysTheRest()
    {
        var context = Context() with
        {
            Tariffs = new Dictionary<(string, string), TariffPrice> { [("REF", "0300")] = new(1_000m, null) },
            CostShares =
            [
                new("DEDUCTIBLE", "SPECIALIST", "AMOUNT", 200m, "IN_NETWORK", null, "PER_LINE", null),
                new("CO_PAYMENT", "SPECIALIST", "PERCENT", 10m, "IN_NETWORK", 50m, "PER_LINE", null),
            ],
            ScheduleLimits = [new("SPECIALIST", 600m, "PER_YEAR")],
        };

        // Claimed 1 200; tariff 1 000 → allowed 1 000; deductible 200; co-payment 10% of 800 = 80, capped at 50; due 750;
        // the annual limit of 600 leaves 600 to pay; the member pays 1 200 − 600 = 600.
        var line = One(context, Line("0300", 1_200m));
        (line.Allowed, line.Deductible, line.CoPayment, line.Paid, line.MemberLiability, line.Reason).ShouldBe((1_000m, 200m, 50m, 600m, 600m, AdjudicationReasons.Limit));
    }

    [Fact]
    public void E_TheDeductibleAlreadyMetThisYear_IsNotTakenAgain_AndAYearlyCoPaymentCapCountsWhatWasTaken()
    {
        var year = new AccumulatorKey(AccumulatorScope.Member, Principal, "SPECIALIST", new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        var context = Context() with
        {
            CostShares =
            [
                new("DEDUCTIBLE", "SPECIALIST", "AMOUNT", 200m, "IN_NETWORK", null, "PER_LINE", null),
                new("CO_PAYMENT", "SPECIALIST", "AMOUNT", 100m, "IN_NETWORK", 250m, "PER_YEAR", null),
            ],
            Accumulators = new Dictionary<AccumulatorKey, AccumulatorState> { [year] = new(null, 0m, 0m, 150m, 200m) },
        };

        // 50 of the deductible is left; the yearly cap of 250 has 50 left.
        var result = AdjudicationEngine.Adjudicate(context, [Line("0300", 1_000m)]);
        var line = result.Lines.Single();
        (line.Deductible, line.CoPayment, line.Paid).ShouldBe((50m, 50m, 900m));
        result.Changes.Single(c => c.Key == year).ShouldBe(new AccumulatorChange(year, null, 900m, 0m, 50m, 50m));
    }

    [Fact]
    public void E_APerClaimCoPaymentCap_IsSharedByTheClaimsLines()
    {
        var context = Context() with { CostShares = [new("CO_PAYMENT", "SPECIALIST", "AMOUNT", 80m, "IN_NETWORK", 100m, "PER_CLAIM", null)] };

        var lines = AdjudicationEngine.Adjudicate(context, [Line("0300", 1_000m), Line("0300", 1_000m) with { LineNo = 2 }]).Lines;
        lines.Select(l => l.CoPayment).ShouldBe([80m, 20m]);
    }

    // (f) Benefit year, family pool and the most restrictive limit.

    [Fact]
    public void F_TheBenefitYearIsTheCalendarYearByDefault_OrTheMembershipYear()
    {
        BenefitPeriods.Year(BenefitPeriods.Calendar, MemberSince, ServiceDate).ShouldBe((new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)));
        BenefitPeriods.Year("MEMBERSHIP_YEAR", MemberSince, ServiceDate).ShouldBe((new DateOnly(2026, 3, 1), new DateOnly(2027, 2, 28)));
        BenefitPeriods.Year("MEMBERSHIP_YEAR", MemberSince, new DateOnly(2026, 2, 1)).ShouldBe((new DateOnly(2025, 3, 1), new DateOnly(2026, 2, 28)));
    }

    [Fact]
    public void F_AFamilyPoolOnThePrincipal_AndTheMembersSubLimit_TheMostRestrictiveWins()
    {
        var year = (new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        var family = new AccumulatorKey(AccumulatorScope.Family, Principal, "SPECIALIST", year.Item1, year.Item2);
        var spouse = new AccumulatorKey(AccumulatorScope.Member, Spouse, "SPECIALIST", year.Item1, year.Item2);
        var context = Context(patient: Spouse) with
        {
            LimitDefinitions = [new("SPECIALIST", "ANNUAL", 2_000m, "PER_FAMILY", null), new("SPECIALIST", "ANNUAL", 5_000m, "PER_MEMBER", "HLT-CORE")],
            Accumulators = new Dictionary<AccumulatorKey, AccumulatorState> { [family] = new(2_000m, 1_700m, 0m, 0m, 0m) },
        };

        // The spouse's own limit has 5 000 left, but the family pool only 300.
        var result = AdjudicationEngine.Adjudicate(context, [Line("0300", 1_000m)]);
        result.Lines.Single().Paid.ShouldBe(300m);
        result.Changes.Select(c => (c.Key, c.Limit, c.Consume)).OrderBy(c => c.Key.Scope).ShouldBe([(family, 2_000m, 300m), (spouse, 5_000m, 300m)]);
    }

    [Fact]
    public void F_APerEventLimit_CapsTheClaim_AndOtherPlansLimitsDoNotApply()
    {
        var context = Context() with
        {
            ScheduleLimits = [new("SPECIALIST", 1_500m, "PER_EVENT")],
            LimitDefinitions = [new("SPECIALIST", "ANNUAL", 100m, "PER_MEMBER", "OTHER-PLAN")],
        };

        AdjudicationEngine.Adjudicate(context, [Line("0300", 1_000m), Line("0300", 1_000m) with { LineNo = 2 }]).Lines.Select(l => l.Paid).ShouldBe([1_000m, 500m]);
    }

    // (g) Exclusions and condition-specific waiting periods with code lists.

    [Fact]
    public void G_ADiagnosisOrProcedureInAnExclusionsCodes_IsRejected()
    {
        var context = Context() with { Exclusions = [new("EXC-COSMETIC", ["L90-L99"], ["0400-0499"])] };

        One(context, Line("0300", 1_000m, 1, "L91.0")).Reason.ShouldBe(AdjudicationReasons.Excluded);
        One(context, Line("0300", 1_000m, 1, "J45")).Outcome.ShouldBe(LineOutcome.Pay);
    }

    [Fact]
    public void G_WaitingPeriods_GeneralWithExceptions_AndConditionSpecificByCode()
    {
        var context = Context() with
        {
            WaitingPeriods =
            [
                new("WP-GEN", "GENERAL", ServiceDate.AddDays(1), ["GP_CONSULT"], [], []),
                new("WP-DIAB", "CONDITION_SPECIFIC", ServiceDate.AddMonths(6), [], ["E10-E14"], []),
            ],
        };

        One(context, Line("0300", 1_000m)).Reason.ShouldBe(AdjudicationReasons.WaitingPeriod);
        One(context, Line("0190", 400m)).Outcome.ShouldBe(LineOutcome.Pay);
        One(context, Line("0190", 400m, 1, "E11.9")).Reason.ShouldBe(AdjudicationReasons.WaitingPeriod);
        One(context with { ServiceDate = ServiceDate.AddDays(1), WaitingPeriods = [context.WaitingPeriods[0]] }, Line("0300", 1_000m)).Outcome.ShouldBe(LineOutcome.Pay);
    }

    // (h) Preauthorisation matching, reservation and consumption.

    [Fact]
    public void H_AMatchedAuthorisation_ReleasesItsReservationForTheLine_AndConsumesWhatIsPaid()
    {
        var preauth = Guid.CreateVersion7();
        var line = Guid.CreateVersion7();
        var year = new AccumulatorKey(AccumulatorScope.Member, Principal, "SPECIALIST", new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        var context = Context() with
        {
            ScheduleLimits = [new("SPECIALIST", 1_500m, "PER_YEAR")],
            Authorised = [new(preauth, line, "0300", 1_000m, 1_000m, 0m, "SPECIALIST")],
            Accumulators = new Dictionary<AccumulatorKey, AccumulatorState> { [year] = new(1_500m, 0m, 1_000m, 0m, 0m) },
            CostShares = [new("CO_PAYMENT", "SPECIALIST", "PERCENT", 10m, "IN_NETWORK", null, "PER_LINE", null)],
        };

        // Allowed 800 (claimed): 800 of the reservation is released, 720 paid after the 10% co-payment; 200 stays reserved.
        var result = AdjudicationEngine.Adjudicate(context, [Line("0300", 800m)]);
        result.Lines.Single().ShouldSatisfyAllConditions(l => l.Paid.ShouldBe(720m), l => l.PreauthLineId.ShouldBe(line));
        result.AuthorisationUses.Single().ShouldBe(new AuthorisationUse(preauth, line, 800m, 720m));
        result.Changes.Single().ShouldBe(new AccumulatorChange(year, 1_500m, 720m, 800m, 0m, 80m));
    }

    [Fact]
    public void H_AboveTheAuthorisedAmount_OrWithoutTheRequiredAuthorisation_TheLinePends_UntilAReviewerAccepts()
    {
        var context = Context() with
        {
            Authorised = [new(Guid.CreateVersion7(), Guid.CreateVersion7(), "0300", 600m, 600m, 0m, "SPECIALIST")],
            PreauthRequired = [new("SPECIALIST", 0m)],
        };

        One(context, Line("0300", 800m)).Reason.ShouldBe(AdjudicationReasons.OverAuthorised);
        One(context with { Authorised = [] }, Line("0300", 800m)).Reason.ShouldBe(AdjudicationReasons.PreauthRequired);
        One(context with { Reviews = new Dictionary<int, ReviewDecision> { [1] = ReviewDecision.Accept } }, Line("0300", 800m)).Paid.ShouldBe(800m);
        One(context with { Reviews = new Dictionary<int, ReviewDecision> { [1] = ReviewDecision.Reject } }, Line("0300", 800m)).Outcome.ShouldBe(LineOutcome.Reject);
    }

    // Duplicates, repeat services, edit rules and high-cost reviews.

    [Fact]
    public void DuplicatesAndRepeatServicesPend_EditRulesReadTheLinesFacts_AndHighCostLinesAreFlagged()
    {
        var context = Context() with
        {
            Prior = [new(Guid.CreateVersion7(), "0300", "SPECIALIST", ServiceDate, "PR-1"), new(Guid.CreateVersion7(), "0190", "GP_CONSULT", ServiceDate.AddDays(-20), "PR-2")],
            Repeats = [new("GP_CONSULT", "P30D")],
            Edits = [new("EDIT-QTY", Expression.Parse("quantity > 3").Value, "REJECT", "QTY_LIMIT")],
            HighCost = [new("HC-SPEC", "SPECIALIST", 900m)],
        };

        One(context, Line("0300", 1_000m)).Reason.ShouldBe(AdjudicationReasons.Duplicate);
        One(context, Line("0190", 400m)).Reason.ShouldBe(AdjudicationReasons.RepeatService);
        One(context with { ServiceDate = ServiceDate.AddDays(11) }, Line("0190", 400m)).Outcome.ShouldBe(LineOutcome.Pay);
        One(context, Line("0195", 200m, 4)).Reason.ShouldBe("QTY_LIMIT");
        One(context with { Prior = [] }, Line("0300", 1_000m)).HighCostRule.ShouldBe("HC-SPEC");
    }

    [Fact]
    public void MemberClaims_ArePricedAtTheReferenceTariff_AndCoveredAtTariff_OrPendWhenThePlanSaysSo()
    {
        var context = Context(treatment: "NOT_COVERED") with
        {
            MemberClaim = true,
            CostShares = [new("CO_PAYMENT", "GP_CONSULT", "PERCENT", 10m, "IN_NETWORK", null, "PER_LINE", null)],
        };

        // Reference tariff 400 (not the contract's 500), the in-network co-payment, and not refused as out of network.
        var priced = One(context, Line("0190", 650m));
        (priced.Outcome, priced.Tariff, priced.Allowed, priced.CoPayment, priced.NetworkStatus).ShouldBe((LineOutcome.Pay, (decimal?)400m, 400m, 40m, (NetworkStatus?)NetworkStatus.OutOfNetwork));

        One(context with { Plan = context.Plan with { MemberClaimBasis = MemberClaimBases.Pend } }, Line("0190", 650m)).Reason.ShouldBe(AdjudicationReasons.MemberClaim);
        One(context, Line("0191", 650m)).Reason.ShouldBe(AdjudicationReasons.NoTariff);
    }

    [Fact]
    public void EditRules_ReadTheClaimHistory_OnlyWithinTheirWindow_EDIT_DUP_30()
    {
        var dup30 = new EditRule("EDIT-DUP-30", Expression.Parse("prior_same_code > 0 AND days_since_same_code <= 30").Value, "PEND", "DUP_30", WindowDays: 30);
        var earlier = new PriorService(Guid.CreateVersion7(), "0300", "SPECIALIST", ServiceDate.AddDays(-20), "PR-2");
        var context = Context() with { Prior = [earlier], Edits = [dup30] };

        One(context, Line("0300", 1_000m)).Reason.ShouldBe("DUP_30");
        One(context with { Prior = [earlier with { ServiceDate = ServiceDate.AddDays(-40) }] }, Line("0300", 1_000m)).Outcome.ShouldBe(LineOutcome.Pay);
        One(context, Line("0190", 400m)).Outcome.ShouldBe(LineOutcome.Pay);

        // Without a window, a rule cannot read history, so it does not apply.
        One(context with { Edits = [dup30 with { WindowDays = null }] }, Line("0300", 1_000m)).Outcome.ShouldBe(LineOutcome.Pay);
    }

    /// <summary>F006: a claim in contribution grace is held: every line pends with CONTRIBUTIONS_UNPAID, nothing is paid or consumed.</summary>
    [Fact]
    public void AClaimInContributionGrace_IsHeld_NotDeclined_F006()
    {
        var result = AdjudicationEngine.Adjudicate(Context() with { HeldForArrears = true }, [Line("0190", 400m), Line("0195", 150m)]);
        result.Lines.ShouldAllBe(l => l.Outcome == LineOutcome.Pend && l.Reason == AdjudicationEngine.HeldForArrearsReason);
        (result.Paid, result.Changes.Count, result.Pended).ShouldBe((0m, 0, true));
    }
}
