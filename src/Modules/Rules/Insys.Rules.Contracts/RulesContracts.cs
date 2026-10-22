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

/// <summary>An underwriting authority row (rules.cfg_authority_matrices): what a role may decide, up to a total sum assured, for a product or all.</summary>
public sealed record UnderwritingAuthority(string RoleCode, string? ProductCode, decimal MaxSumAssured, IReadOnlyList<string> DecisionTypes);

/// <summary>
/// Public query interface: the underwriting authority in force for roles and a product (§7.4), read on Rules' own
/// connection for the current tenant.
/// </summary>
public interface IUnderwritingAuthorities
{
    Task<IReadOnlyList<UnderwritingAuthority>> For(Guid tenantId, IReadOnlyCollection<string> roleCodes, string productCode, DateOnly day, CancellationToken cancellationToken);
}
