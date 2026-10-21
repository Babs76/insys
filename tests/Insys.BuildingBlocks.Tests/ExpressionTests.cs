using Insys.BuildingBlocks.Domain.Expressions;

namespace Insys.BuildingBlocks.Tests;

/// <summary>The configuration condition language (ADR-008), with the static data catalogue's sample expressions.</summary>
public sealed class ExpressionTests
{
    private static bool Eval(string text, params (string Name, object? Value)[] variables) =>
        Expression.Parse(text).Value.Evaluate(variables.ToDictionary(v => v.Name, v => v.Value)).Value;

    [Theory]
    [InlineData(29, "ZA", true)]
    [InlineData(17, "ZA", false)]
    [InlineData(66, "ZA", false)]
    [InlineData(40, "NG", false)]
    public void CatalogueSample_EligibilityRuleSet(int age, string country, bool expected) =>
        Eval("age_next >= 18 AND age_next <= 65 AND resident_country IN (ZA)", ("age_next", age), ("resident_country", country)).ShouldBe(expected);

    [Fact]
    public void CatalogueSample_ReferralRule_ComparesTwoVariables() =>
        Eval("sum_assured > threshold", ("sum_assured", 6_000_000m), ("threshold", 5_000_000m)).ShouldBeTrue();

    [Theory]
    [InlineData("NOT (a = 1 OR b = 2)", false)]
    [InlineData("a BETWEEN 0 AND 1", true)]
    [InlineData("name = 'O''Brien'", true)]
    [InlineData("band IN (18-24, 25-34)", true)]
    [InlineData("band NOT IN (18-24, 25-34)", false)]
    [InlineData("flag = true AND a ≤ 1 AND a ≠ 2", true)]
    [InlineData("a <> 1", false)]
    public void Operators(string text, bool expected) =>
        Eval(text, ("a", 1), ("b", 3), ("name", "O'Brien"), ("band", "25-34"), ("flag", true)).ShouldBe(expected);

    [Fact]
    public void Variables_AreListed() =>
        Expression.Parse("age_next >= 18 AND resident_country IN (ZA) AND age_next < limit").Value.Variables.ShouldBe(["age_next", "limit", "resident_country"]);

    [Theory]
    [InlineData("")]
    [InlineData("a >")]
    [InlineData("a = 1 AND")]
    [InlineData("a IN (1, 2")]
    [InlineData("System.Exit(1)")]
    [InlineData("a = 1; DROP TABLE x")]
    [InlineData("'open")]
    public void InvalidExpressions_AreRejected(string text) =>
        Expression.Parse(text).Error!.Code.ShouldBe(ExpressionErrors.InvalidCode);

    [Fact]
    public void MissingVariable_IsAnError_NotFalse() =>
        Expression.Parse("a > 1").Value.Evaluate(new Dictionary<string, object?>()).Error!.Code.ShouldBe(ExpressionErrors.EvaluationCode);

    [Fact]
    public void ComparingTextWithANumberByOrder_IsAnError() =>
        Expression.Parse("a > 1").Value.Evaluate(new Dictionary<string, object?> { ["a"] = "x" }).Error!.Code.ShouldBe(ExpressionErrors.EvaluationCode);

    [Fact]
    public void NonBooleanResult_IsAnError() =>
        Expression.Parse("a").Value.Evaluate(new Dictionary<string, object?> { ["a"] = 1m }).IsFailure.ShouldBeTrue();
}
