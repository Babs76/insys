using System.Text.Json;
using FluentValidation;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;
using Insys.Rules.Domain.Decisions;

namespace Insys.Rules.Application;

/// <summary>Reads the rules in force for a rule set from the Rules module's static data.</summary>
public interface IRuleSetReader
{
    Task<Result<RuleSetDefinition>> Read(RuleSetKind kind, string code, DateOnly day, CancellationToken cancellationToken);
}

public interface IRuleDecisionRepository
{
    void Add(RuleDecision decision);
}

public sealed record RuleDecisionView(Guid Id, string RulesetCode, int RulesetVersion, string SubjectType, Guid SubjectId, JsonElement Inputs, string Outcome,
    IReadOnlyList<string> FiredRules, DateTimeOffset EvaluatedAt);

public interface IRuleDecisionQueries
{
    Task<RuleDecisionView?> Get(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<RuleDecisionView>> ForSubject(string subjectType, Guid subjectId, int limit, CancellationToken cancellationToken);
}

/// <param name="Code">The ruleset_code for eligibility, or the product code for underwriting.</param>
/// <param name="Inputs">A JSON object of named values; names are the variables the conditions read.</param>
public sealed record EvaluateRuleSet(RuleSetKind Kind, string Code, string SubjectType, Guid SubjectId, JsonElement Inputs) : ICommand<RuleDecisionView>;

public sealed record GetRuleDecision(Guid Id) : IQuery<RuleDecisionView>;

public sealed record ListRuleDecisions(string SubjectType, Guid SubjectId, int Limit) : IQuery<IReadOnlyList<RuleDecisionView>>;

public sealed class EvaluateRuleSetValidator : AbstractValidator<EvaluateRuleSet>
{
    public EvaluateRuleSetValidator()
    {
        RuleFor(c => c.Code).NotEmpty().MaximumLength(15);
        RuleFor(c => c.SubjectType).NotEmpty().MaximumLength(30);
        RuleFor(c => c.SubjectId).NotEmpty();
        RuleFor(c => c.Inputs.ValueKind).Equal(JsonValueKind.Object).WithMessage("Inputs must be a JSON object.");
    }
}

internal sealed class EvaluateRuleSetHandler(IRuleSetReader reader, IRuleDecisionRepository decisions, IClock clock) : ICommandHandler<EvaluateRuleSet, RuleDecisionView>
{
    public async Task<Result<RuleDecisionView>> Handle(EvaluateRuleSet command, CancellationToken cancellationToken)
    {
        var ruleSet = await reader.Read(command.Kind, command.Code, clock.UtcToday, cancellationToken);
        if (ruleSet.IsFailure)
        {
            return ruleSet.Error;
        }

        var inputs = command.Inputs.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone(), StringComparer.Ordinal);
        var json = command.Inputs.GetRawText();
        var decision = RuleDecision.EvaluateRuleSet(Guid.CreateVersion7(), ruleSet.Value, command.SubjectType, command.SubjectId, json, inputs, clock.UtcNow);
        if (decision.IsFailure)
        {
            return decision.Error;
        }

        decisions.Add(decision.Value);
        return RuleDecisionViews.From(decision.Value);
    }
}

internal sealed class GetRuleDecisionHandler(IRuleDecisionQueries queries) : IQueryHandler<GetRuleDecision, RuleDecisionView>
{
    public async Task<Result<RuleDecisionView>> Handle(GetRuleDecision query, CancellationToken cancellationToken) =>
        await queries.Get(query.Id, cancellationToken) is { } view ? view : Error.NotFound(ErrorCodes.NotFound, "The rule decision was not found.");
}

internal sealed class ListRuleDecisionsHandler(IRuleDecisionQueries queries) : IQueryHandler<ListRuleDecisions, IReadOnlyList<RuleDecisionView>>
{
    public async Task<Result<IReadOnlyList<RuleDecisionView>>> Handle(ListRuleDecisions query, CancellationToken cancellationToken) =>
        Result.Success(await queries.ForSubject(query.SubjectType, query.SubjectId, query.Limit, cancellationToken));
}

public static class RuleDecisionViews
{
    public static RuleDecisionView From(RuleDecision d)
    {
        using var inputs = JsonDocument.Parse(d.Inputs);
        return new RuleDecisionView(d.Id, d.RulesetCode, d.RulesetVersion, d.SubjectType, d.SubjectId, inputs.RootElement.Clone(), d.Outcome, d.FiredRules, d.EvaluatedAt);
    }
}
