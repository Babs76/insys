/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Tests
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

namespace Insys.BuildingBlocks.Tests;

/// <summary>Bounded arithmetic in the condition language (ADR-008, Phase 4 review): numbers, dates, year() and today().</summary>
public sealed class ExpressionArithmeticTests
{
    private static readonly DateOnly Today = new(2026, 10, 22);

    private static Domain.Result<bool> Eval(string text, params (string Name, object? Value)[] variables) =>
        Expression.Parse(text).Value.Evaluate(variables.ToDictionary(v => v.Name, v => v.Value), today: Today);

    [Theory]
    [InlineData("a + b = 4", true)]
    [InlineData("a - b = -2", true)]
    [InlineData("a * b = 3", true)]
    [InlineData("b / 2 = 1.5", true)]
    [InlineData("a + b * 2 = 7", true)]
    [InlineData("(a + b) * 2 = 8", true)]
    [InlineData("-a = -1", true)]
    [InlineData("a - -3 = 4", true)]
    [InlineData("a+b=4", true)]
    [InlineData("b BETWEEN a AND a + 2", true)]
    [InlineData("a * 3 > b + 1", false)]
    public void Numbers(string text, bool expected) => Eval(text, ("a", 1m), ("b", 3m)).Value.ShouldBe(expected);

    [Fact]
    public void CatalogueSample_YearAtMostNextYear_Works()
    {
        var rule = Expression.Parse("year <= current_year + 1").Value;
        rule.Evaluate(new Dictionary<string, object?> { ["year"] = 2027m, ["current_year"] = 2026m }).Value.ShouldBeTrue();
        rule.Evaluate(new Dictionary<string, object?> { ["year"] = 2028m, ["current_year"] = 2026m }).Value.ShouldBeFalse();
        Eval("year <= year(today()) + 1", ("year", 2027m)).Value.ShouldBeTrue();
    }

    [Theory]
    [InlineData("inception + 30 = '2026-11-21'", true)]
    [InlineData("inception - 1 = '2026-10-21'", true)]
    [InlineData("today() - inception = 0", true)]
    [InlineData("loss_date - inception < 30", true)]
    [InlineData("loss_date > inception", true)]
    [InlineData("year(loss_date) = 2026", true)]
    [InlineData("year('2028-02-29') = 2028", true)]
    [InlineData("'2028-02-28' + 1 = '2028-02-29'", true)]
    [InlineData("'2028-02-29' + 1 = '2028-03-01'", true)]
    public void Dates(string text, bool expected) =>
        Eval(text, ("inception", new DateOnly(2026, 10, 22)), ("loss_date", "2026-11-05")).Value.ShouldBe(expected);

    [Fact]
    public void DateVariables_StillCompareWithTextDates() =>
        Eval("inception = '2026-10-22' AND inception < '2027-01-01'", ("inception", new DateOnly(2026, 10, 22))).Value.ShouldBeTrue();

    [Fact]
    public void ListValues_WithDashes_AreStillWords() =>
        Eval("band IN (25-34, 35-44) AND age - 10 = 20", ("band", "25-34"), ("age", 30m)).Value.ShouldBeTrue();

    [Theory]
    [InlineData("a / 0 = 1")]
    [InlineData("a + 'x' = 1")]
    [InlineData("inception + 0.5 = inception")]
    [InlineData("inception * 2 = inception")]
    [InlineData("year(a) = 1")]
    [InlineData("inception + 99999999 = inception")]
    [InlineData("a * 79228162514264337593543950335 * 10 > 0")]
    public void BadArithmetic_FailsTheEvaluation_NeverThrows(string text) =>
        Eval(text, ("a", 1m), ("inception", new DateOnly(2026, 10, 22))).Error!.Code.ShouldBe(ExpressionErrors.EvaluationCode);

    [Fact]
    public void Today_WithoutAnEvaluationDate_FailsTheEvaluation() =>
        Expression.Parse("today() > '2020-01-01'").Value.Evaluate(new Dictionary<string, object?>()).Error!.Code.ShouldBe(ExpressionErrors.EvaluationCode);

    [Theory]
    [InlineData("a + = 1")]
    [InlineData("year() = 1")]
    [InlineData("today(1) = 1")]
    [InlineData("a * / 2 = 1")]
    [InlineData("a = 1 +")]
    public void MalformedArithmetic_IsRejected(string text) => Expression.Parse(text).Error!.Code.ShouldBe(ExpressionErrors.InvalidCode);

    [Fact]
    public void Arithmetic_StaysWithinTheLimits()
    {
        var deep = new string('-', Expression.MaxDepth + 1) + "a = 1";
        Expression.Parse(deep).Error!.Code.ShouldBe(ExpressionErrors.InvalidCode);

        var longSum = "a" + string.Concat(Enumerable.Repeat(" + a", 900)) + " > 0";
        Expression.Parse(longSum).IsSuccess.ShouldBeTrue();
        Expression.Parse(longSum).Value.Evaluate(new Dictionary<string, object?> { ["a"] = 1m }, maxSteps: 1_000).Error!.Code.ShouldBe(ExpressionErrors.EvaluationCode);
    }
}
