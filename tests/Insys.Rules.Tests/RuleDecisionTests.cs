using Insys.BuildingBlocks.Domain.Expressions;
using Insys.Rules.Domain.Decisions;

namespace Insys.Rules.Tests;

public sealed class RuleDecisionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly IReadOnlyDictionary<string, object?> None = new Dictionary<string, object?>();

    private static RuleDefinition Rule(string code, string condition, string outcome, int version = 1, Dictionary<string, object?>? constants = null) =>
        new(code, version, Expression.Parse(condition).Value, outcome, null, constants ?? new Dictionary<string, object?>());

    private static readonly RuleSetDefinition Term = RuleSetDefinition.Eligibility("ELIG-LIFE-TERM",
    [
        Rule("ADULT", "age_next >= 18 AND age_next <= 65 AND resident_country IN (ZA)", "ELIGIBLE", version: 3),
        Rule("OLDER", "age_next > 60", "REFER", version: 2),
        Rule("MINOR", "age_next < 18", "INELIGIBLE"),
    ]);

    private static Insys.BuildingBlocks.Domain.Result<RuleDecision> Evaluate(RuleSetDefinition ruleSet, Dictionary<string, object?> inputs) =>
        RuleDecision.EvaluateRuleSet(Guid.CreateVersion7(), ruleSet, "APPLICATION", Guid.CreateVersion7(), "{}", inputs, Now);

    [Fact]
    public void MostSevereFiredOutcome_IsTheDecision_AndTheVersionIsRecorded_LIF002()
    {
        var decision = Evaluate(Term, new() { ["age_next"] = 62, ["resident_country"] = "ZA" }).Value;
        decision.Outcome.ShouldBe("REFER");
        decision.FiredRules.ShouldBe(["ADULT", "OLDER"]);
        decision.RulesetVersion.ShouldBe(3);
        decision.DomainEvents.OfType<RuleDecisionRecorded>().Single().Outcome.ShouldBe("REFER");
    }

    [Fact]
    public void Eligibility_IsNotAssumed_WhenNoRuleFires() =>
        Evaluate(Term, new() { ["age_next"] = 30, ["resident_country"] = "NA" }).Value.Outcome.ShouldBe("INELIGIBLE");

    [Fact]
    public void MissingInput_FailsTheEvaluation_AndNothingIsRecorded() =>
        Evaluate(Term, new() { ["age_next"] = 30 }).Error!.Code.ShouldBe(ExpressionErrors.EvaluationCode);

    [Fact]
    public void Underwriting_ReadsTheRulesThreshold_AndDeclineWins()
    {
        var underwriting = RuleSetDefinition.Underwriting("LIF-TERM",
        [
            Rule("REF-SA-HIGH", "sum_assured > threshold", "REFER", constants: new() { ["threshold"] = 5_000_000m }),
            new RuleDefinition("DCL-AGE-MAX", 1, Expression.Parse("age_next > 65").Value, "DECLINE", "AGE_LIMIT", None),
        ]);

        underwriting.Code.ShouldBe("UW:LIF-TERM");
        Evaluate(underwriting, new() { ["sum_assured"] = 1_000_000, ["age_next"] = 40 }).Value.Outcome.ShouldBe("ACCEPT");
        Evaluate(underwriting, new() { ["sum_assured"] = 6_000_000, ["age_next"] = 40 }).Value.Outcome.ShouldBe("REFER");
        Evaluate(underwriting, new() { ["sum_assured"] = 6_000_000, ["age_next"] = 70 }).Value.Outcome.ShouldBe("DECLINE");
    }

    [Fact]
    public void RuleSet_WithNoRulesInForce_IsNotFound() =>
        Evaluate(RuleSetDefinition.Eligibility("NONE", []), new()).Error!.Code.ShouldBe(RulesErrorCodes.RuleSetNotFound);
}
