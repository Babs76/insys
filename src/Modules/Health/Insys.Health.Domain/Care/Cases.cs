using Insys.BuildingBlocks.Domain;

namespace Insys.Health.Domain.Care;

/// <summary>States from domain-model.json (health › UtilisationCase).</summary>
public enum UtilisationStatus
{
    Open,
    InReview,
    Concluded,
}

public enum UtilisationTrigger
{
    ReviewCase,
    ConcludeCase,
}

/// <summary>States from domain-model.json (health › HealthFraudCase).</summary>
public enum FraudStatus
{
    Opened,
    Investigating,
    Concluded,
}

public enum FraudTrigger
{
    Investigate,
    ConcludeCase,
}

public sealed record UtilisationCaseConcluded(Guid CaseId, Guid MembershipId, string RuleCode, string Outcome, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record FraudCaseConcluded(Guid CaseId, string SubjectType, string SubjectRef, string OutcomeCode, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// The UtilisationCase aggregate (health.utilisation_case): opened by a utilisation or high-cost rule, or by a person,
/// and never closed automatically (§11 K). TODO(spec): utilisation rules' measures are free text and are not evaluated;
/// cases are opened by high-cost thresholds and by people (OQ-061).
/// </summary>
public sealed class UtilisationCase : AggregateRoot<Guid>
{
    public static readonly StateMachine<UtilisationStatus, UtilisationTrigger> Lifecycle = new StateMachine<UtilisationStatus, UtilisationTrigger>()
        .Allow(UtilisationStatus.Open, UtilisationTrigger.ReviewCase, UtilisationStatus.InReview)
        .Allow(UtilisationStatus.InReview, UtilisationTrigger.ConcludeCase, UtilisationStatus.Concluded);

    private UtilisationCase()
    {
    }

    public Guid MembershipId { get; private set; }

    public string RuleCode { get; private set; } = "";

    public UtilisationStatus Status { get; private set; }

    public Guid? ReviewerId { get; private set; }

    public string? Outcome { get; private set; }

    public DateTimeOffset OpenedAt { get; private set; }

    public Guid? ClaimId { get; private set; }

    public DateTimeOffset? ConcludedAt { get; private set; }

    public static UtilisationCase OpenCase(Guid id, Guid membershipId, string ruleCode, Guid? claimId, DateTimeOffset now)
    {
        var opened = new UtilisationCase
        {
            Id = id, MembershipId = membershipId, RuleCode = Guard.MaxLength(ruleCode, 15), ClaimId = claimId, OpenedAt = now, Status = UtilisationStatus.Open,
        };
        opened.MarkChanged();
        return opened;
    }

    public Result ReviewCase(Guid reviewer)
    {
        var result = Fire(UtilisationTrigger.ReviewCase);
        if (result.IsSuccess)
        {
            ReviewerId = reviewer;
        }

        return result;
    }

    public Result ConcludeCase(string outcome, Guid by, DateTimeOffset now)
    {
        if (by != ReviewerId)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "The reviewer concludes the case.");
        }

        var result = Fire(UtilisationTrigger.ConcludeCase);
        if (result.IsSuccess)
        {
            Outcome = Guard.MaxLength(outcome, 20);
            ConcludedAt = now;
            Raise(new UtilisationCaseConcluded(Id, MembershipId, RuleCode, Outcome, now));
        }

        return result;
    }

    private Result Fire(UtilisationTrigger trigger)
    {
        var next = Lifecycle.Fire(Status, trigger);
        if (next.IsFailure)
        {
            return next.Error;
        }

        Status = next.Value;
        MarkChanged();
        return Result.Success();
    }
}

/// <summary>
/// The HealthFraudCase aggregate (health.fraud_case): opened by fraud indicators whose score reaches their threshold;
/// only an investigator concludes it (§11 K).
/// </summary>
public sealed class HealthFraudCase : AggregateRoot<Guid>
{
    public static readonly StateMachine<FraudStatus, FraudTrigger> Lifecycle = new StateMachine<FraudStatus, FraudTrigger>()
        .Allow(FraudStatus.Opened, FraudTrigger.Investigate, FraudStatus.Investigating)
        .Allow(FraudStatus.Investigating, FraudTrigger.ConcludeCase, FraudStatus.Concluded);

    private HealthFraudCase()
    {
    }

    public string SubjectType { get; private set; } = "";

    public string SubjectRef { get; private set; } = "";

    public string[] IndicatorCodes { get; private set; } = [];

    public FraudStatus Status { get; private set; }

    public string? OutcomeCode { get; private set; }

    public DateTimeOffset OpenedAt { get; private set; }

    public Guid? InvestigatorId { get; private set; }

    public DateTimeOffset? ConcludedAt { get; private set; }

    public static HealthFraudCase OpenCase(Guid id, string subjectType, string subjectRef, IReadOnlyList<string> indicatorCodes, DateTimeOffset now)
    {
        var opened = new HealthFraudCase
        {
            Id = id, SubjectType = Guard.MaxLength(subjectType, 12), SubjectRef = Guard.MaxLength(subjectRef, 30), IndicatorCodes = [.. indicatorCodes], OpenedAt = now,
            Status = FraudStatus.Opened,
        };
        opened.MarkChanged();
        return opened;
    }

    public Result Investigate(Guid investigator)
    {
        var result = Fire(FraudTrigger.Investigate);
        if (result.IsSuccess)
        {
            InvestigatorId = investigator;
        }

        return result;
    }

    public Result ConcludeCase(string outcomeCode, Guid by, DateTimeOffset now)
    {
        if (by != InvestigatorId)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "The investigator concludes the case.");
        }

        var result = Fire(FraudTrigger.ConcludeCase);
        if (result.IsSuccess)
        {
            OutcomeCode = Guard.MaxLength(outcomeCode, 15);
            ConcludedAt = now;
            Raise(new FraudCaseConcluded(Id, SubjectType, SubjectRef, OutcomeCode, now));
        }

        return result;
    }

    private Result Fire(FraudTrigger trigger)
    {
        var next = Lifecycle.Fire(Status, trigger);
        if (next.IsFailure)
        {
            return next.Error;
        }

        Status = next.Value;
        MarkChanged();
        return Result.Success();
    }
}
