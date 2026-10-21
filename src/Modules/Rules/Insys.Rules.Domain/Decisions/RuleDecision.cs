using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Domain.Expressions;

namespace Insys.Rules.Domain.Decisions;

/// <summary>The kinds of rule set the engine evaluates (SRS §7.4).</summary>
public enum RuleSetKind
{
    /// <summary>rules.cfg_eligibility_rule_sets, grouped by ruleset_code.</summary>
    Eligibility,

    /// <summary>rules.cfg_acceptance_and_referral_rules and rules.cfg_decline_conditions for a product.</summary>
    Underwriting,
}

/// <param name="Constants">Values the rule's condition may read besides the inputs, such as its threshold.</param>
public sealed record RuleDefinition(string RuleCode, int Version, Expression Condition, string Outcome, string? ReasonCode,
    IReadOnlyDictionary<string, object?> Constants);

/// <summary>
/// The rules in force for one rule set. <paramref name="Severity"/> orders the outcomes, most severe first: the most
/// severe outcome among the rules that fire is the decision, and <paramref name="DefaultOutcome"/> applies when none fire.
/// </summary>
public sealed record RuleSetDefinition(string Code, RuleSetKind Kind, IReadOnlyList<RuleDefinition> Rules, IReadOnlyList<string> Severity, string DefaultOutcome)
{
    public const int MaxCodeLength = 20;

    /// <summary>A rule set's version is the highest version among its rules: any rule change gives a new version.</summary>
    public int Version => Rules.Count == 0 ? 0 : Rules.Max(r => r.Version);

    public static RuleSetDefinition Eligibility(string code, IReadOnlyList<RuleDefinition> rules) =>
        new(code, RuleSetKind.Eligibility, rules, ["INELIGIBLE", "REFER", "ELIGIBLE"], "INELIGIBLE");

    /// <summary>Recorded as "UW:{product}" (ruleset_code is varchar(20) and product codes are up to 15).</summary>
    public static RuleSetDefinition Underwriting(string productCode, IReadOnlyList<RuleDefinition> rules) =>
        new("UW:" + productCode, RuleSetKind.Underwriting, rules, ["DECLINE", "REFER", "ACCEPT"], "ACCEPT");
}

public sealed record RuleDecisionRecorded(Guid DecisionId, string RulesetCode, int RulesetVersion, string SubjectType, Guid SubjectId, string Outcome,
    IReadOnlyList<string> FiredRules, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// The RuleDecision aggregate (rules.decision_log): one evaluation of a rule set, immutable once recorded, with the
/// rule set version, the inputs and the outcome (LIF-002), so the same inputs and version give the same decision.
/// </summary>
public sealed class RuleDecision : AggregateRoot<Guid>
{
    private RuleDecision()
    {
    }

    public string RulesetCode { get; private set; } = "";

    public int RulesetVersion { get; private set; }

    public string SubjectType { get; private set; } = "";

    public Guid SubjectId { get; private set; }

    /// <summary>The inputs as JSON, exactly as evaluated.</summary>
    public string Inputs { get; private set; } = "{}";

    public string Outcome { get; private set; } = "";

    public string[] FiredRules { get; private set; } = [];

    public DateTimeOffset EvaluatedAt { get; private set; }

    /// <summary>Command EvaluateRuleSet. A rule that cannot be evaluated (a missing input, say) fails the evaluation, and nothing is recorded.</summary>
    public static Result<RuleDecision> EvaluateRuleSet(Guid id, RuleSetDefinition ruleSet, string subjectType, Guid subjectId, string inputsJson,
        IReadOnlyDictionary<string, object?> inputs, DateTimeOffset now)
    {
        if (ruleSet.Rules.Count == 0)
        {
            return Error.NotFound(RulesErrorCodes.RuleSetNotFound, $"No rules of rule set {ruleSet.Code} are in force.");
        }

        var fired = new List<RuleDefinition>();
        foreach (var rule in ruleSet.Rules.OrderBy(r => r.RuleCode, StringComparer.Ordinal))
        {
            var variables = rule.Constants.Count == 0 ? inputs : Merge(inputs, rule.Constants);
            var holds = rule.Condition.Evaluate(variables);
            if (holds.IsFailure)
            {
                return Error.Validation(holds.Error.Code, $"Rule {rule.RuleCode}: {holds.Error.Message}");
            }

            if (holds.Value)
            {
                fired.Add(rule);
            }
        }

        var outcome = fired.Count == 0
            ? ruleSet.DefaultOutcome
            : fired.Select(r => r.Outcome).OrderBy(o => IndexOf(ruleSet.Severity, o)).First();
        var decision = new RuleDecision
        {
            Id = Guard.NotEmpty(id),
            RulesetCode = Guard.MaxLength(ruleSet.Code, RuleSetDefinition.MaxCodeLength),
            RulesetVersion = ruleSet.Version,
            SubjectType = Guard.MaxLength(Guard.NotNullOrWhiteSpace(subjectType), 30),
            SubjectId = Guard.NotEmpty(subjectId),
            Inputs = inputsJson,
            Outcome = outcome,
            FiredRules = fired.Select(r => r.RuleCode).ToArray(),
            EvaluatedAt = now,
        };
        decision.Raise(new RuleDecisionRecorded(id, decision.RulesetCode, decision.RulesetVersion, decision.SubjectType, subjectId, outcome, decision.FiredRules, now));
        decision.MarkChanged();
        return decision;
    }

    private static int IndexOf(IReadOnlyList<string> severity, string outcome)
    {
        for (var i = 0; i < severity.Count; i++)
        {
            if (severity[i] == outcome)
            {
                return i;
            }
        }

        return severity.Count;
    }

    private static Dictionary<string, object?> Merge(IReadOnlyDictionary<string, object?> inputs, IReadOnlyDictionary<string, object?> constants)
    {
        var merged = new Dictionary<string, object?>(inputs, StringComparer.Ordinal);
        foreach (var (name, value) in constants)
        {
            merged[name] = value;
        }

        return merged;
    }
}

public static class RulesErrorCodes
{
    [ErrorCode(404, "error.rules.ruleset_not_found")]
    public const string RuleSetNotFound = "RULESET_NOT_FOUND";
}
