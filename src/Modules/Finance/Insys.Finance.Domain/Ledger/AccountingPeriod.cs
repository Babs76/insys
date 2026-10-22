using Insys.BuildingBlocks.Domain;

namespace Insys.Finance.Domain.Ledger;

/// <summary>States from domain-model.json (finance › AccountingPeriod), stored in finance.accounting_period.status.</summary>
public enum PeriodStatus
{
    Future,
    Open,
    SoftClose,
    Closed,
}

public enum PeriodTrigger
{
    OpenPeriod,
    SoftClosePeriod,
    ReopenedWithApproval,
    ClosePeriod,
}

public sealed record AccountingPeriodClosed(Guid PeriodId, string PeriodCode, bool Final, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>Reopening a soft-closed period asks Workflow for a second person's approval (ADR-009).</summary>
public sealed record PeriodReopenRequested(Guid PeriodId, string PeriodCode, Guid RequestedBy, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// The AccountingPeriod aggregate (finance.accounting_period, V0012). The accounting calendar (static data) defines the
/// period's code and dates; this aggregate owns its state. Only one open period covers a date (also a database
/// constraint), and only an open period accepts postings; a closed period accepts none.
/// </summary>
public sealed class AccountingPeriod : AggregateRoot<Guid>
{
    public static readonly StateMachine<PeriodStatus, PeriodTrigger> Lifecycle = new StateMachine<PeriodStatus, PeriodTrigger>()
        .Allow(PeriodStatus.Future, PeriodTrigger.OpenPeriod, PeriodStatus.Open)
        .Allow(PeriodStatus.Open, PeriodTrigger.SoftClosePeriod, PeriodStatus.SoftClose)
        .Allow(PeriodStatus.SoftClose, PeriodTrigger.ReopenedWithApproval, PeriodStatus.Open)
        .Allow(PeriodStatus.SoftClose, PeriodTrigger.ClosePeriod, PeriodStatus.Closed);

    private AccountingPeriod()
    {
    }

    public string PeriodCode { get; private set; } = "";

    public DateOnly StartDate { get; private set; }

    public DateOnly EndDate { get; private set; }

    public PeriodStatus Status { get; private set; }

    public Guid? ClosedBy { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    public Guid? ReopenRequestedBy { get; private set; }

    public DateTimeOffset? ReopenRequestedAt { get; private set; }

    /// <summary>A period from the accounting calendar, in the Future state.</summary>
    public static AccountingPeriod Define(Guid id, string periodCode, DateOnly start, DateOnly end)
    {
        var period = new AccountingPeriod
        {
            Id = Guard.NotEmpty(id),
            PeriodCode = Guard.MaxLength(Guard.NotNullOrWhiteSpace(periodCode), 7),
            StartDate = start,
            EndDate = end >= start ? end : throw new ArgumentException("A period ends on or after it starts.", nameof(end)),
            Status = PeriodStatus.Future,
        };
        period.MarkChanged();
        return period;
    }

    public bool Covers(DateOnly date) => StartDate <= date && date <= EndDate;

    /// <summary>Only an open period accepts postings (§15.1).</summary>
    public bool AcceptsPostingOn(DateOnly date) => Status == PeriodStatus.Open && Covers(date);

    public Result OpenPeriod() => Fire(PeriodTrigger.OpenPeriod);

    public Result SoftClosePeriod(Guid closedBy, DateTimeOffset now)
    {
        var result = Fire(PeriodTrigger.SoftClosePeriod);
        if (result.IsSuccess)
        {
            ClosedBy = closedBy;
            ClosedAt = now;
            Raise(new AccountingPeriodClosed(Id, PeriodCode, Final: false, now));
        }

        return result;
    }

    public Result ClosePeriod(Guid closedBy, DateTimeOffset now)
    {
        if (ReopenRequestedBy is not null)
        {
            return Error.Conflict(FinanceErrorCodes.ReopenPending, $"Reopening {PeriodCode} is waiting for approval.");
        }

        var result = Fire(PeriodTrigger.ClosePeriod);
        if (result.IsSuccess)
        {
            ClosedBy = closedBy;
            ClosedAt = now;
            Raise(new AccountingPeriodClosed(Id, PeriodCode, Final: true, now));
        }

        return result;
    }

    /// <summary>Asks for a soft-closed period to be reopened. Nothing changes until a second person approves it.</summary>
    public Result RequestReopen(Guid requestedBy, DateTimeOffset now)
    {
        if (Status != PeriodStatus.SoftClose)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Only a soft-closed period is reopened; {PeriodCode} is {Status}.");
        }

        if (ReopenRequestedBy is not null)
        {
            return Error.Conflict(FinanceErrorCodes.ReopenPending, $"Reopening {PeriodCode} is already waiting for approval.");
        }

        ReopenRequestedBy = requestedBy;
        ReopenRequestedAt = now;
        Raise(new PeriodReopenRequested(Id, PeriodCode, requestedBy, now));
        MarkChanged();
        return Result.Success();
    }

    /// <summary>Workflow's decision on a reopen request (transition "Reopened with approval"). The checker is never the requester.</summary>
    public Result DecideReopen(bool approved, Guid checkerId)
    {
        if (ReopenRequestedBy is not { } requester)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"No reopening of {PeriodCode} is waiting for approval.");
        }

        if (checkerId == requester)
        {
            return MakerChecker.EnsureDifferentPeople(requester.ToString(), checkerId.ToString());
        }

        ReopenRequestedBy = null;
        ReopenRequestedAt = null;
        if (approved)
        {
            var result = Fire(PeriodTrigger.ReopenedWithApproval);
            if (result.IsFailure)
            {
                return result;
            }

            ClosedBy = null;
            ClosedAt = null;
        }

        MarkChanged();
        return Result.Success();
    }

    private Result Fire(PeriodTrigger trigger)
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

/// <summary>Chooses the posting rule for an event: exactly one rule in force for its transaction type whose condition holds.</summary>
public static class PostingRuleResolver
{
    public static Result<PostingRule> Resolve(IReadOnlyList<PostingRule> rulesInForce, BusinessEvent businessEvent)
    {
        var matches = new List<PostingRule>();
        foreach (var rule in rulesInForce.Where(r => r.TransactionType == businessEvent.TransactionType))
        {
            if (rule.Condition is null)
            {
                matches.Add(rule);
                continue;
            }

            var holds = rule.Condition.Evaluate(businessEvent.Variables);
            if (holds.IsFailure)
            {
                return Error.Validation(holds.Error.Code, $"Posting rule {rule.RuleCode} v{rule.Version}: {holds.Error.Message}");
            }

            if (holds.Value)
            {
                matches.Add(rule);
            }
        }

        return matches.Count switch
        {
            1 => matches[0],
            0 => Error.Validation(FinanceErrorCodes.PostingRuleNotFound,
                $"No posting rule in force on {businessEvent.AccountingDate:yyyy-MM-dd} applies to {businessEvent.TransactionType}."),
            _ => Error.Validation(FinanceErrorCodes.PostingRuleAmbiguous,
                $"Posting rules {string.Join(", ", matches.Select(m => $"{m.RuleCode} v{m.Version}"))} all apply to {businessEvent.TransactionType}."),
        };
    }
}
