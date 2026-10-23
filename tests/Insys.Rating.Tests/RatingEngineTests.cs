/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Rating.Tests
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
using Insys.Rating.Domain.Calculations;

namespace Insys.Rating.Tests;

/// <summary>
/// The reference rating engine (ADR-007). These are ENGINEERING CASES: premiums worked by hand from the engine's own
/// rules, to show it does what ADR-007 says. They are not actuarial validation. Actuary-supplied cases, with
/// expected premiums from the actuaries' own models, are still needed (OQ-052; docs/rating/test-cases.md).
/// </summary>
[Trait("Category", "EngineeringCase")]
public sealed class RatingEngineTests
{
    private static readonly RatingConfiguration Motor = new(
        "GEN-MOTOR-PV",
        4,
        [
            new FactorDefinition("DRIVER_AGE", "INTEGER", "risk.driver.age", new[] { "18-24", "25-34", "35-59", "60+" }.Select(Band.Parse).ToList()),
            new FactorDefinition("VEHICLE_GROUP", "CODE", "vehicle_group", []),
        ],
        [new RatingTable("MOT-BASE", 9, ["VEHICLE_GROUP", "DRIVER_AGE"], [new(["G3", "25-34"], 0.0285m), new(["G3", "18-24"], 0.04m)], "NONE")],
        [new("MOT-BASE", 1, "FACTOR"), new("FLEET_FACTOR", 1.1m, "FACTOR"), new("COMM_LOAD", 10, "PERCENT"), new("POLICY_FEE", 75, "AMOUNT")],
        [new("NCB-3Y", "DISCOUNT", "PERCENT", 15, 40, 2), new("NCB-5Y", "DISCOUNT", "PERCENT", 30, 40, 3)],
        new Dictionary<string, PremiumBounds> { ["MONTHLY"] = new(150, null) },
        [("ZA", new TaxRate("LEVY", 0.5m, "GROSS_PREMIUM", "ZAR-2DP"))],
        [new Rounding("ZAR-2DP", 0.01m, "HALF_UP", "FINAL_PREMIUM")]);

    private static RatingInput Input(Dictionary<string, object?> values) => new("ZAR", "MONTHLY", values);

    private static Dictionary<string, object?> Risk(decimal sumInsured, decimal age = 29, string[]? adjustments = null) => new()
    {
        ["sum_insured"] = sumInsured,
        ["risk.driver.age"] = age,
        ["vehicle_group"] = "G3",
        ["adjustments"] = adjustments ?? ["NCB-3Y", "NCB-5Y"],
        ["jurisdiction"] = "ZA",
    };

    [Fact]
    public void Premium_FollowsTheReferenceSteps()
    {
        // 250 000 × 0.0285 = 7 125; × 1.1 (FLEET_FACTOR) × 1.1 (COMM_LOAD 10 %) = 8 621.25.
        // NCB-3Y: 15 % = 1 293.1875 → 7 328.0625. NCB-5Y: 30 % = 2 198.41875, capped at 40 % of 8 621.25 less 1 293.1875 = 2 155.3125 → 5 172.75.
        // + 75 fee = 5 247.75. Levy 0.5 % = 26.24 (rounded) → 5 273.99.
        var result = RatingEngine.Calculate(Motor, Input(Risk(250_000)));

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Message : null);
        result.Value.Premium.ShouldBe(5273.99m);
        result.Value.RatingVersion.ShouldBe(9);
        result.Value.Lines.Select(l => (l.Component, l.FactorCode, l.Amount)).ShouldBe(
        [
            ("EXPOSURE", null, 250_000m),
            ("TABLE", "MOT-BASE", 7125m),
            ("FACTOR", "FLEET_FACTOR", 7837.5m),
            ("PERCENT", "COMM_LOAD", 8621.25m),
            ("DISCOUNT", "NCB-3Y", 7328.0625m),
            ("DISCOUNT", "NCB-5Y", 5172.75m),
            ("FEE", "POLICY_FEE", 5247.75m),
            ("ROUNDING", "ZAR-2DP", 5247.75m),
            ("TAX", "LEVY", 5273.99m),
        ]);
        result.Value.Lines.Single(l => l.Component == "TABLE").FactorValue.ShouldBe("G3,25-34");
    }

    [Fact]
    public void SameInputsAndConfiguration_GiveTheSameResult_LIF002() =>
        RatingEngine.Calculate(Motor, Input(Risk(123_456))).Value.ShouldBeEquivalentTo(RatingEngine.Calculate(Motor, Input(Risk(123_456))).Value);

    [Fact]
    public void PremiumBelowTheMinimum_IsRaisedToIt()
    {
        var result = RatingEngine.Calculate(Motor, Input(Risk(1_000))).Value;
        result.Lines.ShouldContain(l => l.Component == "MINIMUM" && l.Amount == 150m);
        result.Premium.ShouldBe(150.75m);
    }

    [Fact]
    public void FactorValue_IsBanded() =>
        RatingEngine.Calculate(Motor, Input(Risk(100_000, age: 19, adjustments: []))).Value.Lines.Single(l => l.Component == "TABLE").Amount.ShouldBe(4000m);

    [Fact]
    public void MissingInput_IsAnError_NotAZeroPremium()
    {
        var values = Risk(100_000);
        values.Remove("vehicle_group");
        RatingEngine.Calculate(Motor, Input(values)).Error!.Code.ShouldBe(RatingErrorCodes.InputMissing);
    }

    [Fact]
    public void CombinationWithoutATableValue_IsAnError() =>
        RatingEngine.Calculate(Motor, Input(Risk(100_000, age: 70))).Error!.Code.ShouldBe(RatingErrorCodes.NoTableValue);

    [Fact]
    public void AdjustmentNotInForce_IsAnError() =>
        RatingEngine.Calculate(Motor, Input(Risk(100_000, adjustments: ["NCB-9Y"]))).Error!.Code.ShouldBe(RatingErrorCodes.ConfigurationIncomplete);

    [Fact]
    public void Currency_WithoutAFinalRoundingRule_IsIncompleteConfiguration() =>
        RatingEngine.Calculate(Motor with { Roundings = [] }, Input(Risk(100_000))).Error!.Code.ShouldBe(RatingErrorCodes.ConfigurationIncomplete);

    [Fact]
    public void LinearTable_Interpolates()
    {
        var config = Motor with
        {
            Factors = [new FactorDefinition("AGE", "INTEGER", "age", [])],
            Tables = [new RatingTable("MORT", 3, ["AGE"], [new(["30"], 0.001m), new(["40"], 0.003m)], "LINEAR")],
            Parameters = [new("MORT", 1, "FACTOR")],
            Taxes = [],
        };
        var result = RatingEngine.Calculate(config, Input(new() { ["sum_assured"] = 1_000_000m, ["age"] = 35m })).Value;
        result.Premium.ShouldBe(2000m);
        result.RatingVersion.ShouldBe(3);
    }

    [Theory]
    [InlineData("HALF_UP", 2.345, 2.35)]
    [InlineData("HALF_EVEN", 2.345, 2.34)]
    [InlineData("UP", 2.341, 2.35)]
    [InlineData("DOWN", 2.349, 2.34)]
    public void Rounding_FollowsItsMethodAndStep(string method, double value, double expected) =>
        new Rounding("R", 0.01m, method, "FINAL_PREMIUM").Apply((decimal)value).ShouldBe((decimal)expected);

    [Fact]
    public void StepOrder_IsConfiguredPerProductVersion()
    {
        // Fees before discounts: 7 125 × 1.1 × 1.1 = 8 621.25; + 75 = 8 696.25; NCB-3Y 15 % = 1 304.4375 → 7 391.8125.
        var feesFirst = Motor with
        {
            Parameters = [.. Motor.Parameters, new PremiumParameter("STEP_FEES", 2.5m, "FACTOR")],
            Taxes = [],
        };
        var result = RatingEngine.Calculate(feesFirst, Input(Risk(250_000, adjustments: ["NCB-3Y"]))).Value;
        result.Lines.Select(l => l.Component).ShouldBe(["EXPOSURE", "TABLE", "FACTOR", "PERCENT", "FEE", "DISCOUNT", "ROUNDING"]);
        result.Lines.Single(l => l.Component == "DISCOUNT").Amount.ShouldBe(7391.8125m);
    }

    [Fact]
    public void DefaultStepOrder_IsTheReferenceOrder() =>
        StepOrder.For([]).ShouldBe([RatingStep.Factors, RatingStep.Percent, RatingStep.Adjustments, RatingStep.Fees, RatingStep.Bounds]);

    [Theory]
    [InlineData("STEP_TAX", 1)]
    [InlineData("STEP_FEES", 1)]
    public void StepOrder_ThatIsUnknownOrAmbiguous_IsIncompleteConfiguration(string code, decimal position) =>
        RatingEngine.Calculate(Motor with { Parameters = [.. Motor.Parameters, new PremiumParameter(code, position, "FACTOR")] }, Input(Risk(100_000)))
            .Error!.Code.ShouldBe(RatingErrorCodes.ConfigurationIncomplete);
}
