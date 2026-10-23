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

/// <summary>ADR-008 limits, and a fuzz test: malformed input fails with an expression error, never with another exception.</summary>
public sealed class ExpressionLimitTests
{
    private static readonly Dictionary<string, object?> Values = new()
    {
        ["age"] = 40m, ["country"] = "ZA", ["flag"] = true, ["nothing"] = null, ["x"] = 1m, ["a.b"] = 2.5m, ["big"] = double.NaN,
    };

    [Fact]
    public void Expression_LongerThanTheLimit_IsInvalid() =>
        Expression.Parse("age > 1" + string.Concat(Enumerable.Repeat(" OR age > 1", 400))).Error!.Code.ShouldBe(ExpressionErrors.InvalidCode);

    [Fact]
    public void Nesting_UpToTheLimit_IsAllowed_AndDeeperIsInvalid()
    {
        string Nested(int depth) => new string('(', depth) + "age > 1" + new string(')', depth);
        Expression.Parse(Nested(Expression.MaxDepth)).IsSuccess.ShouldBeTrue();
        Expression.Parse(Nested(Expression.MaxDepth + 1)).Error!.Code.ShouldBe(ExpressionErrors.InvalidCode);
        Expression.Parse(string.Concat(Enumerable.Repeat("NOT ", Expression.MaxDepth + 1)) + "flag").Error!.Code.ShouldBe(ExpressionErrors.InvalidCode);
    }

    [Fact]
    public void Evaluation_BeyondItsStepBudget_Fails()
    {
        var expression = Expression.Parse("x IN (" + string.Join(",", Enumerable.Range(2, 200)) + ") OR age > 1").Value;
        expression.Evaluate(Values).Value.ShouldBeTrue();
        expression.Evaluate(Values, maxSteps: 100).Error!.Code.ShouldBe(ExpressionErrors.EvaluationCode);
    }

    [Theory]
    [InlineData("x IN (")]
    [InlineData("x IN (1,")]
    [InlineData("x NOT")]
    [InlineData("age > 1.2.3")]
    [InlineData("age > 99999999999999999999999999999999999")]
    [InlineData("(((((")]
    [InlineData("'unclosed")]
    [InlineData("age BETWEEN 1")]
    [InlineData(")")]
    [InlineData("NOT")]
    public void MalformedInput_IsInvalid(string text) => Expression.Parse(text).Error!.Code.ShouldBe(ExpressionErrors.InvalidCode);

    [Fact]
    public void ValueThatCannotBeCompared_FailsEvaluation() =>
        Expression.Parse("big > 1").Value.Evaluate(Values).Error!.Code.ShouldBe(ExpressionErrors.EvaluationCode);

    /// <summary>Thousands of random and mutated inputs, from a fixed seed so a failure can be reproduced.</summary>
    [Fact]
    public void Fuzz_MalformedInput_FailsSafely()
    {
        var random = new Random(20261022);
        string[] fragments =
        [
            "age", "country", "flag", "nothing", "x", "a.b", "missing", "AND", "OR", "NOT", "IN", "BETWEEN", "(", ")", ",", "=", "!=", "<>", "<", "<=", "≤",
            ">", ">=", "≥", "≠", "'ZA'", "'it''s'", "'", "1", "-1", "2.5", "1.2.3", "25-34", "ZA", "true", "false", "null", "99999999999999999999999999999",
            "\0", "\n", "€", "--", "''", "((", "))", "IN (", "NOT IN (",
        ];
        string[] valid = ["age >= 18 AND age <= 65 AND country IN (ZA)", "NOT flag OR x BETWEEN 0 AND 2", "a.b > 2 AND country NOT IN ('NA', 'BW')"];
        var cases = new List<string>();
        for (var i = 0; i < 3000; i++)
        {
            cases.Add(string.Join(" ", Enumerable.Range(0, random.Next(1, 12)).Select(_ => fragments[random.Next(fragments.Length)])));
            cases.Add(new string(Enumerable.Range(0, random.Next(1, 40)).Select(_ => (char)random.Next(0, 0x2200)).ToArray()));
            var source = valid[random.Next(valid.Length)];
            var cut = random.Next(source.Length);
            cases.Add(random.Next(3) switch
            {
                0 => source[..cut],
                1 => source.Remove(cut, 1),
                _ => source.Insert(cut, fragments[random.Next(fragments.Length)]),
            });
        }

        foreach (var text in cases)
        {
            var parsed = Should.NotThrow(() => Expression.Parse(text), text);
            if (parsed.IsFailure)
            {
                parsed.Error!.Code.ShouldBe(ExpressionErrors.InvalidCode, text);
                continue;
            }

            var evaluated = Should.NotThrow(() => parsed.Value.Evaluate(Values), text);
            if (evaluated.IsFailure)
            {
                evaluated.Error!.Code.ShouldBe(ExpressionErrors.EvaluationCode, text);
            }
        }
    }
}
