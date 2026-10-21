using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Domain.Expressions;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Rules.Application;
using Insys.Rules.Domain.Decisions;
using Insys.Rules.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;

namespace Insys.Rules.Infrastructure.Persistence;

internal sealed class RuleDecisionRepository(RulesDbContext db) : IRuleDecisionRepository
{
    public void Add(RuleDecision decision) => db.Decisions.Add(decision);
}

internal sealed class RuleDecisionQueries(RulesDbContext db) : IRuleDecisionQueries
{
    public async Task<RuleDecisionView?> Get(Guid id, CancellationToken cancellationToken) =>
        await db.Decisions.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, cancellationToken) is { } decision ? RuleDecisionViews.From(decision) : null;

    public async Task<IReadOnlyList<RuleDecisionView>> ForSubject(string subjectType, Guid subjectId, int limit, CancellationToken cancellationToken) =>
        (await db.Decisions.AsNoTracking().Where(d => d.SubjectType == subjectType && d.SubjectId == subjectId).OrderByDescending(d => d.EvaluatedAt).Take(limit)
            .ToListAsync(cancellationToken)).Select(RuleDecisionViews.From).ToList();
}

/// <summary>Builds rule sets from the rules in force: eligibility rule sets, and acceptance, referral and decline rules.</summary>
internal sealed class RuleSetReader(RulesDbContext db) : IRuleSetReader
{
    private static readonly IReadOnlyDictionary<string, object?> None = new Dictionary<string, object?>();

    public async Task<Result<RuleSetDefinition>> Read(RuleSetKind kind, string code, DateOnly day, CancellationToken cancellationToken)
    {
        var rules = new List<RuleDefinition>();
        if (kind == RuleSetKind.Eligibility)
        {
            var rows = await db.Set<EligibilityRuleSets>().AsNoTracking().InForce(Lifecycle<EligibilityRuleSets>(), day).Where(r => r.RulesetCode == code)
                .ToListAsync(cancellationToken);
            foreach (var row in rows)
            {
                var condition = Parse(row.Code, row.Conditions);
                if (condition.IsFailure)
                {
                    return condition.Error;
                }

                rules.Add(new RuleDefinition(row.Code, row.Version, condition.Value, row.Outcome, null, None));
            }

            return RuleSetDefinition.Eligibility(code, rules);
        }

        // TODO(spec): acceptance and referral rules carry no product; all of the tenant's rules apply to every product (OQ-051).
        foreach (var row in await db.Set<AcceptanceAndReferralRules>().AsNoTracking().InForce(Lifecycle<AcceptanceAndReferralRules>(), day).ToListAsync(cancellationToken))
        {
            var condition = Parse(row.Code, row.Condition);
            if (condition.IsFailure)
            {
                return condition.Error;
            }

            rules.Add(new RuleDefinition(row.Code, row.Version, condition.Value, row.Outcome, null, new Dictionary<string, object?> { ["threshold"] = row.Threshold }));
        }

        foreach (var row in await db.Set<DeclineConditions>().AsNoTracking().InForce(Lifecycle<DeclineConditions>(), day)
                     .Where(r => r.ProductCode == null || r.ProductCode == code).ToListAsync(cancellationToken))
        {
            var condition = Parse(row.Code, row.Expression);
            if (condition.IsFailure)
            {
                return condition.Error;
            }

            rules.Add(new RuleDefinition(row.Code, row.Version, condition.Value, "DECLINE", row.ReasonCode, None));
        }

        return RuleSetDefinition.Underwriting(code, rules);
    }

    private static Result<Expression> Parse(string ruleCode, string text)
    {
        var parsed = Expression.Parse(text);
        return parsed.IsFailure ? Error.Validation(parsed.Error.Code, $"Rule {ruleCode} has an invalid condition: {parsed.Error.Message}") : parsed;
    }

    private static StaticLifecycle Lifecycle<T>() => RulesStaticData.Descriptors.Single(d => d.EntityType == typeof(T)).Lifecycle;
}

internal sealed class RulesEventMapper : IDomainEventMapper
{
    public IIntegrationEvent? Map(IDomainEvent domainEvent) => domainEvent switch
    {
        Domain.Decisions.RuleDecisionRecorded e => new Contracts.RuleDecisionRecorded(e.DecisionId, e.RulesetCode, e.RulesetVersion, e.SubjectType, e.SubjectId,
            e.Outcome, e.FiredRules),
        _ => null,
    };
}
