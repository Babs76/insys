using Insys.BuildingBlocks.Contracts;

namespace Insys.Rules.Contracts;

public static class RulesPermissions
{
    public const string DecisionRead = "rule_decision.read";
    public const string DecisionCreate = "rule_decision.create";
}

/// <summary>A rule set was evaluated. The inputs stay in the decision log; they are not in the event (INT-007).</summary>
[IntegrationEvent("rules.RuleDecisionRecorded")]
public sealed record RuleDecisionRecorded(Guid DecisionId, string RulesetCode, int RulesetVersion, string SubjectType, Guid SubjectId, string Outcome,
    IReadOnlyList<string> FiredRules) : IIntegrationEvent;
