using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Domain.Expressions;

namespace Insys.Finance.Domain.Ledger;

/// <summary>
/// A version of finance.cfg_posting_rules in force on the accounting date (FIN-004): for a transaction type whose
/// condition holds, debit one account and credit another.
/// </summary>
public sealed record PostingRule(string RuleCode, int Version, string TransactionType, Expression? Condition, string DebitAccount, string CreditAccount);

/// <summary>A business event to post (FIN-001, FIN-005): what happened, for how much, and to which business record.</summary>
public sealed record BusinessEvent(Guid SourceEventId, string SourceDomain, string TransactionType, DateOnly AccountingDate, DateOnly TransactionDate,
    decimal Amount, string Currency, string BusinessRefType, Guid BusinessRefId, IReadOnlyDictionary<string, object?> Variables);

/// <summary>The rate to the tenant's base currency on the accounting date; 1 when the event is in the base currency.</summary>
public sealed record ExchangeRate(string BaseCurrency, decimal Rate);

/// <summary>Entity SubledgerEntry (finance.subledger_entry): one side of a journal, with the posting rule version used (FIN-004).</summary>
public sealed class SubledgerEntry : Entity<Guid>, ITenantScoped
{
    private SubledgerEntry()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid JournalId { get; private set; }

    public short LineNo { get; private set; }

    public string AccountCode { get; private set; } = "";

    public decimal Debit { get; private set; }

    public decimal Credit { get; private set; }

    public string Currency { get; private set; } = "";

    /// <summary>The amount in the tenant's base currency.</summary>
    public decimal BaseAmount { get; private set; }

    /// <summary>The rate to the base currency; null when the entry is in the base currency.</summary>
    public decimal? FxRate { get; private set; }

    public DateOnly AccountingDate { get; private set; }

    public DateOnly TransactionDate { get; private set; }

    public string BusinessRefType { get; private set; } = "";

    public Guid BusinessRefId { get; private set; }

    public string PostingRuleCode { get; private set; } = "";

    public int PostingRuleVersion { get; private set; }

    /// <summary>The same entry with debit and credit swapped, at the original rate and rule version, on the reversal's accounting date.</summary>
    internal SubledgerEntry Reversed(Guid journalId, short lineNo, DateOnly accountingDate) => new()
    {
        Id = Guid.CreateVersion7(),
        JournalId = journalId,
        LineNo = lineNo,
        AccountCode = AccountCode,
        Debit = Credit,
        Credit = Debit,
        Currency = Currency,
        BaseAmount = BaseAmount,
        FxRate = FxRate,
        AccountingDate = accountingDate,
        TransactionDate = TransactionDate,
        BusinessRefType = BusinessRefType,
        BusinessRefId = BusinessRefId,
        PostingRuleCode = PostingRuleCode,
        PostingRuleVersion = PostingRuleVersion,
    };

    internal static SubledgerEntry Create(Guid journalId, short lineNo, string account, decimal debit, decimal credit, string currency, ExchangeRate rate,
        DateOnly accountingDate, DateOnly transactionDate, string businessRefType, Guid businessRefId, string ruleCode, int ruleVersion) => new()
    {
        Id = Guid.CreateVersion7(),
        JournalId = journalId,
        LineNo = lineNo,
        AccountCode = account,
        Debit = debit,
        Credit = credit,
        Currency = currency,
        BaseAmount = Math.Round((debit + credit) * rate.Rate, 2, MidpointRounding.ToEven),
        FxRate = currency == rate.BaseCurrency ? null : rate.Rate,
        AccountingDate = accountingDate,
        TransactionDate = transactionDate,
        BusinessRefType = businessRefType,
        BusinessRefId = businessRefId,
        PostingRuleCode = ruleCode,
        PostingRuleVersion = ruleVersion,
    };
}

public sealed record SubledgerEntryPosted(Guid JournalId, string JournalNumber, DateOnly AccountingDate, string TransactionType, decimal Amount, string Currency,
    string BusinessRefType, Guid BusinessRefId, string PostingRuleCode, int PostingRuleVersion, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record JournalReversed(Guid JournalId, Guid ReversalJournalId, DateOnly AccountingDate, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// The Journal aggregate (finance.journal and finance.subledger_entry). Debits equal credits; it is traceable to its
/// source business event (FIN-001); it is never overwritten once posted (FIN-002), and a correction is a reversal
/// journal that references the original (FIN-003); it records the posting rule version used (FIN-004); it is posted
/// only into an open accounting period (§15.1). The database checks the balance again at commit (V0014).
/// </summary>
public sealed class Journal : AggregateRoot<Guid>
{
    private readonly List<SubledgerEntry> _lines = [];

    private Journal()
    {
    }

    public string JournalNumber { get; private set; } = "";

    public DateOnly AccountingDate { get; private set; }

    public string PeriodCode { get; private set; } = "";

    /// <summary>The business event posted; the inbox makes it one journal per event (DBA-007).</summary>
    public Guid SourceEventId { get; private set; }

    public string SourceDomain { get; private set; } = "";

    /// <summary>Always POSTED: a reversed journal is the one a later journal reverses, since posted rows never change.</summary>
    public string Status { get; private set; } = "POSTED";

    public DateTimeOffset PostedAt { get; private set; }

    public Guid? ReversesJournalId { get; private set; }

    public IReadOnlyList<SubledgerEntry> Lines => _lines;

    /// <summary>Command PostBusinessEvent: the posting rule's debit and credit for the event's amount.</summary>
    public static Result<Journal> PostBusinessEvent(Guid id, BusinessEvent businessEvent, PostingRule rule, AccountingPeriod period, ExchangeRate rate,
        DateTimeOffset now)
    {
        if (!period.AcceptsPostingOn(businessEvent.AccountingDate))
        {
            return Error.Conflict(FinanceErrorCodes.PeriodNotOpen,
                $"Accounting period {period.PeriodCode} is {period.Status}; it does not accept postings on {businessEvent.AccountingDate:yyyy-MM-dd}.");
        }

        if (businessEvent.Amount <= 0 || decimal.Round(businessEvent.Amount, 2) != businessEvent.Amount)
        {
            return Error.Validation(FinanceErrorCodes.JournalUnbalanced, "A posted amount is positive, to two decimal places.");
        }

        var journal = New(id, businessEvent.SourceEventId, businessEvent.SourceDomain, businessEvent.AccountingDate, period.PeriodCode, null, now);
        journal.Add(rule.DebitAccount, businessEvent.Amount, 0, businessEvent, rule, rate);
        journal.Add(rule.CreditAccount, 0, businessEvent.Amount, businessEvent, rule, rate);
        var balanced = journal.CheckBalanced();
        if (balanced.IsFailure)
        {
            return balanced.Error;
        }

        journal.Raise(new SubledgerEntryPosted(id, journal.JournalNumber, businessEvent.AccountingDate, businessEvent.TransactionType, businessEvent.Amount,
            businessEvent.Currency, businessEvent.BusinessRefType, businessEvent.BusinessRefId, rule.RuleCode, rule.Version, now));
        return journal;
    }

    /// <summary>
    /// Command ReverseJournal (FIN-003): a new journal with every line's debit and credit swapped, on its own accounting
    /// date in an open period, referencing the original. The original is never changed.
    /// </summary>
    public static Result<Journal> ReverseJournal(Guid id, Journal original, bool alreadyReversed, Guid sourceEventId, DateOnly accountingDate, AccountingPeriod period,
        DateTimeOffset now)
    {
        if (alreadyReversed)
        {
            return Error.Conflict(FinanceErrorCodes.AlreadyReversed, $"Journal {original.JournalNumber} is already reversed.");
        }

        if (original.ReversesJournalId is not null)
        {
            return Error.Conflict(FinanceErrorCodes.AlreadyReversed, "A reversal journal is not reversed; post a new business event instead.");
        }

        if (!period.AcceptsPostingOn(accountingDate))
        {
            return Error.Conflict(FinanceErrorCodes.PeriodNotOpen, $"Accounting period {period.PeriodCode} does not accept postings on {accountingDate:yyyy-MM-dd}.");
        }

        var reversal = New(id, sourceEventId, original.SourceDomain, accountingDate, period.PeriodCode, original.Id, now);
        foreach (var line in original.Lines.OrderBy(l => l.LineNo))
        {
            reversal._lines.Add(line.Reversed(id, (short)(reversal._lines.Count + 1), accountingDate));
        }

        var balanced = reversal.CheckBalanced();
        if (balanced.IsFailure)
        {
            return balanced.Error;
        }

        reversal.Raise(new JournalReversed(original.Id, id, accountingDate, now));
        return reversal;
    }

    private static Journal New(Guid id, Guid sourceEventId, string sourceDomain, DateOnly accountingDate, string periodCode, Guid? reverses, DateTimeOffset now)
    {
        var journal = new Journal
        {
            Id = Guard.NotEmpty(id),
            JournalNumber = "JNL-" + id.ToString("N")[..16].ToUpperInvariant(),
            AccountingDate = accountingDate,
            PeriodCode = periodCode,
            SourceEventId = Guard.NotEmpty(sourceEventId),
            SourceDomain = Guard.MaxLength(Guard.NotNullOrWhiteSpace(sourceDomain), 10),
            PostedAt = now,
            ReversesJournalId = reverses,
        };
        journal.MarkChanged();
        return journal;
    }

    private void Add(string account, decimal debit, decimal credit, BusinessEvent e, PostingRule rule, ExchangeRate rate) =>
        _lines.Add(SubledgerEntry.Create(Id, (short)(_lines.Count + 1), account, debit, credit, e.Currency, rate, e.AccountingDate, e.TransactionDate,
            e.BusinessRefType, e.BusinessRefId, rule.RuleCode, rule.Version));

    /// <summary>Debits equal credits, in one currency, with at least two lines and one side per line.</summary>
    private Result CheckBalanced()
    {
        if (_lines.Count < 2 || _lines.Select(l => l.Currency).Distinct().Count() != 1
            || _lines.Any(l => l.Debit < 0 || l.Credit < 0 || (l.Debit == 0) == (l.Credit == 0))
            || _lines.Sum(l => l.Debit) != _lines.Sum(l => l.Credit))
        {
            return Error.Validation(FinanceErrorCodes.JournalUnbalanced, "A journal has at least two lines in one currency, and its debits equal its credits.");
        }

        return Result.Success();
    }
}

public static class FinanceErrorCodes
{
    [ErrorCode(422, "error.finance.journal_unbalanced")]
    public const string JournalUnbalanced = "JOURNAL_UNBALANCED";

    [ErrorCode(409, "error.finance.period_not_open")]
    public const string PeriodNotOpen = "PERIOD_NOT_OPEN";

    [ErrorCode(409, "error.finance.already_reversed")]
    public const string AlreadyReversed = "JOURNAL_ALREADY_REVERSED";

    [ErrorCode(422, "error.finance.posting_rule_not_found")]
    public const string PostingRuleNotFound = "POSTING_RULE_NOT_FOUND";

    [ErrorCode(422, "error.finance.posting_rule_ambiguous")]
    public const string PostingRuleAmbiguous = "POSTING_RULE_AMBIGUOUS";

    [ErrorCode(422, "error.finance.account_not_usable")]
    public const string AccountNotUsable = "ACCOUNT_NOT_USABLE";

    [ErrorCode(422, "error.finance.exchange_rate_missing")]
    public const string ExchangeRateMissing = "EXCHANGE_RATE_MISSING";

    [ErrorCode(404, "error.finance.period_not_defined")]
    public const string PeriodNotDefined = "PERIOD_NOT_DEFINED";

    [ErrorCode(409, "error.finance.reopen_pending")]
    public const string ReopenPending = "PERIOD_REOPEN_PENDING";
}
