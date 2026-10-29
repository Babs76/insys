/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Finance
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
using System.Text.Json;
using FluentValidation;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;
using Insys.Finance.Contracts;
using Insys.Finance.Domain.Ledger;
using Insys.Tenancy.Contracts;

namespace Insys.Finance.Application;

public interface IJournalRepository
{
    Task<Journal?> Get(Guid id, CancellationToken cancellationToken);

    Task<bool> IsReversed(Guid journalId, CancellationToken cancellationToken);

    Task<Journal?> BySourceEvent(Guid sourceEventId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Journal>> ForBusinessRef(string businessRefType, Guid businessRefId, CancellationToken cancellationToken);

    /// <summary>BR-043: journals matching the search, by ID after <paramref name="afterId"/>, oldest first.</summary>
    Task<IReadOnlyList<Journal>> Search(JournalSearch search, Guid? afterId, int limit, CancellationToken cancellationToken);

    /// <summary>BR-043: the journal reversing each of the journals, for those reversed.</summary>
    Task<IReadOnlyDictionary<Guid, Guid>> ReversalsOf(IReadOnlyCollection<Guid> journalIds, CancellationToken cancellationToken);

    Task<IReadOnlyList<(string Account, string Currency, decimal Debit, decimal Credit)>> TrialBalance(string periodCode, CancellationToken cancellationToken);

    void Add(Journal journal);
}

public interface IAccountingPeriods
{
    /// <summary>The period whose state covers the date, if any has been defined.</summary>
    Task<AccountingPeriod?> Covering(DateOnly date, CancellationToken cancellationToken);

    Task<AccountingPeriod?> ByCode(string periodCode, CancellationToken cancellationToken);

    Task<AccountingPeriod?> Get(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<AccountingPeriod>> All(CancellationToken cancellationToken);

    void Add(AccountingPeriod period);

    /// <summary>
    /// The ledger's monthly partitions for the period's dates (V0089): finance.journal and finance.subledger_entry are
    /// partitioned on accounting_date, and an open period, however far ahead, can be posted to.
    /// </summary>
    Task EnsurePartitions(DateOnly from, DateOnly until, CancellationToken cancellationToken);
}

/// <summary>The Finance module's static data in force: posting rules, accounts, rates and the accounting calendar.</summary>
public interface IPostingConfiguration
{
    Task<Result<IReadOnlyList<PostingRule>>> Rules(DateOnly day, CancellationToken cancellationToken);

    /// <summary>Whether the account exists in the chart of accounts in force, is active, and takes the currency.</summary>
    Task<bool> AccountUsable(string accountCode, string currency, DateOnly day, CancellationToken cancellationToken);

    /// <summary>The latest spot rate on or before the day from one currency to another.</summary>
    Task<decimal?> Rate(string from, string to, DateOnly day, CancellationToken cancellationToken);

    /// <summary>The calendar's definition of a period: its dates.</summary>
    Task<(DateOnly Start, DateOnly End)?> CalendarPeriod(string periodCode, DateOnly day, CancellationToken cancellationToken);

    /// <summary>BR-045: every period of the accounting calendar in force (finance.cfg_accounting_calendar_and_periods).</summary>
    Task<IReadOnlyList<(string PeriodCode, DateOnly Start, DateOnly End)>> CalendarPeriods(DateOnly day, CancellationToken cancellationToken);
}

public interface IPostingExceptions
{
    Task<PostingException?> Get(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<PostingException>> List(string? status, Guid? afterId, int limit, CancellationToken cancellationToken);

    void Add(PostingException exception);
}

public sealed record EntryView(int LineNo, string AccountCode, decimal Debit, decimal Credit, string Currency, decimal BaseAmount, decimal? FxRate,
    DateOnly TransactionDate, string BusinessRefType, Guid BusinessRefId, string PostingRuleCode, int PostingRuleVersion);

/// <param name="ReversedByJournalId">The journal that reverses this one (BR-043); null when it is not reversed.</param>
/// <param name="BaseCurrency">The tenant's base currency on the accounting date, which each line's baseAmount is in (BR-043).</param>
public sealed record JournalView(Guid Id, string JournalNumber, DateOnly AccountingDate, string PeriodCode, Guid SourceEventId, string SourceDomain, [property: OneOf("POSTED")] string Status,
    DateTimeOffset PostedAt, Guid? ReversesJournalId, IReadOnlyList<EntryView> Lines, Guid? ReversedByJournalId = null, string? BaseCurrency = null)
{
    public static JournalView From(Journal j) => new(j.Id, j.JournalNumber, j.AccountingDate, j.PeriodCode, j.SourceEventId, j.SourceDomain, j.Status, j.PostedAt,
        j.ReversesJournalId, j.Lines.OrderBy(l => l.LineNo).Select(l => new EntryView(l.LineNo, l.AccountCode, l.Debit, l.Credit, l.Currency, l.BaseAmount, l.FxRate,
            l.TransactionDate, l.BusinessRefType, l.BusinessRefId, l.PostingRuleCode, l.PostingRuleVersion)).ToList());
}

/// <summary>A search of journals (BR-043): by business record (both given), journal number, period, or accounting dates; each optional.</summary>
public sealed record JournalSearch(string? BusinessRefType, Guid? BusinessRefId, string? JournalNumber, string? PeriodCode, DateOnly? AccountingFrom, DateOnly? AccountingTo);

/// <param name="Id">Null for a period of the calendar not yet acted on (Future, BR-045).</param>
/// <param name="ReopenRequestedAt">When a reopen of the soft-closed period was asked for (BR-045).</param>
/// <param name="ReopenApprovalId">The Workflow approval of the reopen while it waits for a decision (BR-045).</param>
public sealed record PeriodView(Guid? Id, string PeriodCode, DateOnly StartDate, DateOnly EndDate, PeriodStatus Status, Guid? ClosedBy, DateTimeOffset? ClosedAt,
    Guid? ReopenRequestedBy, long RowVersion, Workflow.Contracts.LatestRejection? LatestRejection = null, DateTimeOffset? ReopenRequestedAt = null,
    Guid? ReopenApprovalId = null)
{
    public static PeriodView From(AccountingPeriod p) =>
        new(p.Id, p.PeriodCode, p.StartDate, p.EndDate, p.Status, p.ClosedBy, p.ClosedAt, p.ReopenRequestedBy, p.RowVersion, null, p.ReopenRequestedAt);

    /// <summary>A period of the calendar no one has opened yet.</summary>
    public static PeriodView Future(string periodCode, DateOnly start, DateOnly end) => new(null, periodCode, start, end, PeriodStatus.Future, null, null, null, 0);
}

/// <param name="Amount">The failed event's amount, in <paramref name="Currency"/> (BR-044).</param>
/// <param name="AccountingDate">The accounting date it was to be posted on (BR-044).</param>
/// <param name="BusinessRefType">The business record it is about (BR-044), with <paramref name="BusinessRefId"/>.</param>
public sealed record PostingExceptionView(Guid Id, Guid SourceEventId, string EventType, string Error, [property: OneOf("OPEN", "RESOLVED")] string Status, Guid? ResolvedBy, DateTimeOffset? ResolvedAt,
    Guid? JournalId, long RowVersion, decimal? Amount = null, string? Currency = null, DateOnly? AccountingDate = null, string? TransactionType = null,
    string? BusinessRefType = null, Guid? BusinessRefId = null);

public sealed record TrialBalanceLine(string AccountCode, string Currency, decimal Debit, decimal Credit, decimal Balance);

/// <summary>Command ReverseJournal (FIN-003): a reversal journal on an accounting date in an open period.</summary>
public sealed record ReverseJournal(Guid JournalId, DateOnly AccountingDate, string Reason) : ICommand<JournalView>;

public enum PeriodAction
{
    Open,
    SoftClose,
    Close,
    RequestReopen,
}

/// <summary>Commands OpenPeriod, SoftClosePeriod and ClosePeriod, and a request to reopen (approved in Workflow).</summary>
public sealed record ChangePeriod(string PeriodCode, PeriodAction Action) : ICommand<PeriodView>;

/// <summary>Posts a failed business event again, optionally on another accounting date in an open period.</summary>
public sealed record RetryPosting(Guid ExceptionId, DateOnly? AccountingDate) : ICommand<PostingExceptionView>;

public sealed record GetJournal(Guid JournalId) : IQuery<JournalView>;

public sealed record ListJournals(JournalSearch Search, Guid? AfterId, int Limit) : IQuery<IReadOnlyList<JournalView>>;

public sealed record ListPeriods : IQuery<IReadOnlyList<PeriodView>>;

/// <summary>BR-045: one period, acted on or (Future) only in the calendar.</summary>
public sealed record GetPeriod(string PeriodCode) : IQuery<PeriodView>;

public sealed record ListPostingExceptions(string? Status, int Limit, Guid? AfterId = null) : IQuery<IReadOnlyList<PostingExceptionView>>;

/// <summary>BR-044: one posting exception.</summary>
public sealed record GetPostingException(Guid ExceptionId) : IQuery<PostingExceptionView>;

public sealed class ListJournalsValidator : AbstractValidator<ListJournals>
{
    public ListJournalsValidator()
    {
        RuleFor(q => q.Search).Must(s => (s.BusinessRefType is null) == (s.BusinessRefId is null))
            .WithMessage("businessRefType and businessRefId are given together, or neither.");
        RuleFor(q => q.Search).Must(s => s.AccountingFrom is null || s.AccountingTo is null || s.AccountingFrom <= s.AccountingTo)
            .WithMessage("accountingFrom is on or before accountingTo.");
        RuleFor(q => q.Limit).InclusiveBetween(1, 200);
    }
}

public sealed record GetTrialBalance(string PeriodCode) : IQuery<IReadOnlyList<TrialBalanceLine>>;

public sealed class ReverseJournalValidator : AbstractValidator<ReverseJournal>
{
    public ReverseJournalValidator() => RuleFor(c => c.Reason).NotEmpty().MaximumLength(500);
}

internal static class Users
{
    public static Result<Guid> Require(IExecutionContext execution) =>
        execution.ActorType == ActorType.User && Guid.TryParse(execution.ActorId, out var id)
            ? id
            : Error.Forbidden(ErrorCodes.Forbidden, "This is done by an identified user.");
}

/// <summary>
/// Posts business events: resolves the posting rule version in force on the accounting date (FIN-004), checks the
/// accounts and the open period, converts to the base currency, and builds a balanced journal.
/// </summary>
public sealed class PostingService(IJournalRepository journals, IAccountingPeriods periods, IPostingConfiguration configuration, ITenantBaseCurrency baseCurrency,
    ITenantContext tenant, IClock clock)
{
    public async Task<Result<Journal>> Post(BusinessEvent businessEvent, CancellationToken cancellationToken)
    {
        var day = businessEvent.AccountingDate;
        var period = await periods.Covering(day, cancellationToken);
        if (period is null)
        {
            return Error.Conflict(FinanceErrorCodes.PeriodNotOpen, $"No accounting period covering {day:yyyy-MM-dd} has been opened.");
        }

        var rules = await configuration.Rules(day, cancellationToken);
        if (rules.IsFailure)
        {
            return rules.Error;
        }

        var rule = PostingRuleResolver.Resolve(rules.Value, businessEvent);
        if (rule.IsFailure)
        {
            return rule.Error;
        }

        foreach (var account in new[] { rule.Value.DebitAccount, rule.Value.CreditAccount })
        {
            if (!await configuration.AccountUsable(account, businessEvent.Currency, day, cancellationToken))
            {
                return Error.Validation(FinanceErrorCodes.AccountNotUsable,
                    $"Account {account} (posting rule {rule.Value.RuleCode} v{rule.Value.Version}) is not active for {businessEvent.Currency} on {day:yyyy-MM-dd}.");
            }
        }

        var rate = await Rate(businessEvent.Currency, day, cancellationToken);
        if (rate.IsFailure)
        {
            return rate.Error;
        }

        var journal = Journal.PostBusinessEvent(Guid.CreateVersion7(), businessEvent, rule.Value, period, rate.Value, clock.UtcNow);
        if (journal.IsSuccess)
        {
            journals.Add(journal.Value);
        }

        return journal;
    }

    private async Task<Result<ExchangeRate>> Rate(string currency, DateOnly day, CancellationToken cancellationToken)
    {
        var baseCode = await baseCurrency.BaseCurrency(tenant.RequireTenantId(), day, cancellationToken);
        if (baseCode is null)
        {
            return Error.Validation(FinanceErrorCodes.ExchangeRateMissing, "The tenant has no base currency in force (tenant locale defaults).");
        }

        if (baseCode == currency)
        {
            return new ExchangeRate(baseCode, 1m);
        }

        return await configuration.Rate(currency, baseCode, day, cancellationToken) is { } rate
            ? new ExchangeRate(baseCode, rate)
            : Error.Validation(FinanceErrorCodes.ExchangeRateMissing, $"No {currency} to {baseCode} spot rate is in force on or before {day:yyyy-MM-dd}.");
    }
}

/// <summary>Posting from an event: a failure is kept as a posting exception, in the same transaction, never lost or retried silently (FIN-007).</summary>
public sealed class EventPoster(PostingService posting, IPostingExceptions exceptions, IClock clock)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task Post(BusinessEvent businessEvent, string eventType, CancellationToken cancellationToken)
    {
        var result = await posting.Post(businessEvent, cancellationToken);
        if (result.IsFailure)
        {
            exceptions.Add(PostingException.Record(Guid.CreateVersion7(), businessEvent.SourceEventId, eventType, $"{result.Error.Code}: {result.Error.Message}",
                JsonSerializer.Serialize(StoredEvent.From(businessEvent), Json), clock.UtcNow));
        }
    }
}

/// <summary>A business event as kept with a posting exception. Variables are kept as text, numbers or booleans.</summary>
public sealed record StoredEvent(Guid SourceEventId, string SourceDomain, string TransactionType, DateOnly AccountingDate, DateOnly TransactionDate, decimal Amount,
    string Currency, string BusinessRefType, Guid BusinessRefId, Dictionary<string, JsonElement> Variables)
{
    public static StoredEvent From(BusinessEvent e) => new(e.SourceEventId, e.SourceDomain, e.TransactionType, e.AccountingDate, e.TransactionDate, e.Amount, e.Currency,
        e.BusinessRefType, e.BusinessRefId, e.Variables.ToDictionary(v => v.Key, v => JsonSerializer.SerializeToElement(v.Value, EventPoster.Json)));

    public BusinessEvent ToBusinessEvent(DateOnly? accountingDate) => new(SourceEventId, SourceDomain, TransactionType, accountingDate ?? AccountingDate, TransactionDate,
        Amount, Currency, BusinessRefType, BusinessRefId, Variables.ToDictionary(v => v.Key, v => (object?)v.Value, StringComparer.Ordinal));
}

internal sealed class ReverseJournalHandler(IJournalRepository journals, IAccountingPeriods periods, IExecutionContext execution, IClock clock, IAuditTrail audit)
    : ICommandHandler<ReverseJournal, JournalView>
{
    public async Task<Result<JournalView>> Handle(ReverseJournal command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (await journals.Get(command.JournalId, cancellationToken) is not { } original)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The journal was not found.");
        }

        if (await periods.Covering(command.AccountingDate, cancellationToken) is not { } period)
        {
            return Error.Conflict(FinanceErrorCodes.PeriodNotOpen, $"No accounting period covering {command.AccountingDate:yyyy-MM-dd} has been opened.");
        }

        audit.Record(new AuditEntry("FINANCE", "ReverseJournal", nameof(Journal), original.Id.ToString(), new Dictionary<string, string?> { ["reason"] = command.Reason }));
        var reversal = Journal.ReverseJournal(Guid.CreateVersion7(), original, await journals.IsReversed(original.Id, cancellationToken), Guid.CreateVersion7(),
            command.AccountingDate, period, clock.UtcNow);
        if (reversal.IsFailure)
        {
            return reversal.Error;
        }

        journals.Add(reversal.Value);
        return JournalView.From(reversal.Value);
    }
}

internal sealed class ChangePeriodHandler(IAccountingPeriods periods, IPostingConfiguration configuration, IExecutionContext execution, IBusinessDate businessDate, IClock clock, IAuditTrail audit)
    : ICommandHandler<ChangePeriod, PeriodView>
{
    public async Task<Result<PeriodView>> Handle(ChangePeriod command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        var period = await periods.ByCode(command.PeriodCode, cancellationToken);
        if (period is null)
        {
            if (await configuration.CalendarPeriod(command.PeriodCode, businessDate.Today, cancellationToken) is not { } dates)
            {
                return Error.NotFound(FinanceErrorCodes.PeriodNotDefined, $"The accounting calendar in force has no period {command.PeriodCode}.");
            }

            period = AccountingPeriod.Define(Guid.CreateVersion7(), command.PeriodCode, dates.Start, dates.End);
            periods.Add(period);
        }

        audit.Record(new AuditEntry("FINANCE", $"Period{command.Action}", nameof(AccountingPeriod), period.Id.ToString(),
            new Dictionary<string, string?> { ["period"] = command.PeriodCode }));
        if (command.Action == PeriodAction.Open)
        {
            await periods.EnsurePartitions(period.StartDate, period.EndDate, cancellationToken);
        }

        var result = command.Action switch
        {
            PeriodAction.Open => period.OpenPeriod(),
            PeriodAction.SoftClose => period.SoftClosePeriod(user.Value, clock.UtcNow),
            PeriodAction.Close => period.ClosePeriod(user.Value, clock.UtcNow),
            _ => period.RequestReopen(user.Value, clock.UtcNow),
        };
        return result.IsFailure ? result.Error : PeriodView.From(period);
    }
}

internal sealed class RetryPostingHandler(IPostingExceptions exceptions, PostingService posting, IExecutionContext execution, IClock clock, IAuditTrail audit)
    : ICommandHandler<RetryPosting, PostingExceptionView>
{
    public async Task<Result<PostingExceptionView>> Handle(RetryPosting command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (await exceptions.Get(command.ExceptionId, cancellationToken) is not { } exception)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The posting exception was not found.");
        }

        if (exception.Status != "OPEN")
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "The posting exception is already resolved.");
        }

        var stored = JsonSerializer.Deserialize<StoredEvent>(exception.BusinessEvent, EventPoster.Json)!;
        audit.Record(new AuditEntry("FINANCE", "RetryPosting", nameof(PostingException), exception.Id.ToString()));
        var journal = await posting.Post(stored.ToBusinessEvent(command.AccountingDate), cancellationToken);
        if (journal.IsFailure)
        {
            return journal.Error;
        }

        exception.Resolve(user.Value, journal.Value.Id, clock.UtcNow);
        return View(exception);
    }

    /// <summary>The exception, with the failed event's amount, accounting date and business record (BR-044) from the event it keeps.</summary>
    internal static PostingExceptionView View(PostingException e)
    {
        var stored = JsonSerializer.Deserialize<StoredEvent>(e.BusinessEvent, EventPoster.Json);
        return new(e.Id, e.SourceEventId, e.EventType, e.Error, e.Status, e.ResolvedBy, e.ResolvedAt, e.JournalId, e.RowVersion, stored?.Amount, stored?.Currency,
            stored?.AccountingDate, stored?.TransactionType, stored?.BusinessRefType, stored?.BusinessRefId);
    }
}

/// <summary>Journal views with the journal that reverses each (BR-043) and the base currency on its accounting date.</summary>
internal sealed class JournalViews(IJournalRepository journals, ITenantBaseCurrency baseCurrency, ITenantContext tenant)
{
    public async Task<IReadOnlyList<JournalView>> Of(IReadOnlyList<Journal> found, CancellationToken cancellationToken)
    {
        var reversals = await journals.ReversalsOf(found.Select(j => j.Id).ToList(), cancellationToken);
        var currencies = new Dictionary<DateOnly, string?>();
        var views = new List<JournalView>();
        foreach (var journal in found)
        {
            if (!currencies.TryGetValue(journal.AccountingDate, out var currency))
            {
                currencies[journal.AccountingDate] = currency = await baseCurrency.BaseCurrency(tenant.RequireTenantId(), journal.AccountingDate, cancellationToken);
            }

            views.Add(JournalView.From(journal) with { ReversedByJournalId = reversals.TryGetValue(journal.Id, out var reversal) ? reversal : null, BaseCurrency = currency });
        }

        return views;
    }
}

internal sealed class GetJournalHandler(IJournalRepository journals, ITenantBaseCurrency baseCurrency, ITenantContext tenant) : IQueryHandler<GetJournal, JournalView>
{
    public async Task<Result<JournalView>> Handle(GetJournal query, CancellationToken cancellationToken) =>
        await journals.Get(query.JournalId, cancellationToken) is { } journal
            ? (await new JournalViews(journals, baseCurrency, tenant).Of([journal], cancellationToken))[0]
            : Error.NotFound(ErrorCodes.NotFound, "The journal was not found.");
}

/// <summary>Journals of a business record, or found by number, period and accounting dates without one (BR-043), oldest first.</summary>
internal sealed class ListJournalsHandler(IJournalRepository journals, ITenantBaseCurrency baseCurrency, ITenantContext tenant) : IQueryHandler<ListJournals, IReadOnlyList<JournalView>>
{
    public async Task<Result<IReadOnlyList<JournalView>>> Handle(ListJournals query, CancellationToken cancellationToken) =>
        Result.Success(await new JournalViews(journals, baseCurrency, tenant).Of(await journals.Search(query.Search, query.AfterId, query.Limit, cancellationToken),
            cancellationToken));
}

/// <summary>
/// The periods, each with how its latest reopen request's approval ended without being granted, if one did (OQ-113). A
/// period has no view of its own, so the list carries it; a tenant has few periods.
/// </summary>
internal sealed class ListPeriodsHandler(IAccountingPeriods periods, IPostingConfiguration configuration, Workflow.Contracts.IApprovalHistory approvals,
    IBusinessDate businessDate) : IQueryHandler<ListPeriods, IReadOnlyList<PeriodView>>, IQueryHandler<GetPeriod, PeriodView>
{
    /// <summary>The periods acted on, then the calendar's periods no one has opened yet, as Future (BR-045), in start date order.</summary>
    public async Task<Result<IReadOnlyList<PeriodView>>> Handle(ListPeriods query, CancellationToken cancellationToken)
    {
        var views = new List<PeriodView>();
        var acted = await periods.All(cancellationToken);
        foreach (var period in acted)
        {
            views.Add(await View(period, cancellationToken));
        }

        var known = acted.Select(p => p.PeriodCode).ToHashSet(StringComparer.Ordinal);
        views.AddRange((await configuration.CalendarPeriods(businessDate.Today, cancellationToken)).Where(c => !known.Contains(c.PeriodCode))
            .Select(c => PeriodView.Future(c.PeriodCode, c.Start, c.End)));
        return views.OrderBy(v => v.StartDate).ThenBy(v => v.PeriodCode, StringComparer.Ordinal).ToList();
    }

    public async Task<Result<PeriodView>> Handle(GetPeriod query, CancellationToken cancellationToken)
    {
        if (await periods.ByCode(query.PeriodCode, cancellationToken) is { } period)
        {
            return await View(period, cancellationToken);
        }

        return await configuration.CalendarPeriod(query.PeriodCode, businessDate.Today, cancellationToken) is { } calendar
            ? PeriodView.Future(query.PeriodCode, calendar.Start, calendar.End)
            : Error.NotFound(ErrorCodes.NotFound, "The accounting period was not found.");
    }

    private async Task<PeriodView> View(AccountingPeriod period, CancellationToken cancellationToken) => PeriodView.From(period) with
    {
        LatestRejection = await approvals.Latest(FinanceApprovals.PeriodBusinessRefType, period.Id, cancellationToken),
        ReopenApprovalId = period.ReopenRequestedBy is null ? null : await approvals.Waiting(FinanceApprovals.PeriodBusinessRefType, period.Id, cancellationToken),
    };
}

internal sealed class ListPostingExceptionsHandler(IPostingExceptions exceptions) : IQueryHandler<ListPostingExceptions, IReadOnlyList<PostingExceptionView>>,
    IQueryHandler<GetPostingException, PostingExceptionView>
{
    public async Task<Result<IReadOnlyList<PostingExceptionView>>> Handle(ListPostingExceptions query, CancellationToken cancellationToken) =>
        (await exceptions.List(query.Status, query.AfterId, query.Limit, cancellationToken)).Select(RetryPostingHandler.View).ToList();

    public async Task<Result<PostingExceptionView>> Handle(GetPostingException query, CancellationToken cancellationToken) =>
        await exceptions.Get(query.ExceptionId, cancellationToken) is { } exception
            ? RetryPostingHandler.View(exception)
            : Error.NotFound(ErrorCodes.NotFound, "The posting exception was not found.");
}

internal sealed class GetTrialBalanceHandler(IJournalRepository journals) : IQueryHandler<GetTrialBalance, IReadOnlyList<TrialBalanceLine>>
{
    public async Task<Result<IReadOnlyList<TrialBalanceLine>>> Handle(GetTrialBalance query, CancellationToken cancellationToken) =>
        (await journals.TrialBalance(query.PeriodCode, cancellationToken)).Select(t => new TrialBalanceLine(t.Account, t.Currency, t.Debit, t.Credit, t.Debit - t.Credit))
        .ToList();
}
