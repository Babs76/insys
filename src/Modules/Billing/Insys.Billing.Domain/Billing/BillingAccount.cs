using Insys.BuildingBlocks.Domain;

namespace Insys.Billing.Domain.Billing;

/// <summary>States from domain-model.json (billing › BillingAccount).</summary>
public enum AccountStatus
{
    Active,
    InArrears,
    Closed,
}

public enum AccountTrigger
{
    DetectArrears,
    ArrearsCleared,
    CloseAccount,
}

public enum InstalmentStatus
{
    Scheduled,
    Invoiced,
    Cancelled,
}

/// <summary>A billing frequency (billing.ref_billing_frequencies): how many instalments a year.</summary>
public sealed record BillingFrequency(string Code, int PeriodsPerYear);

/// <summary>An arrears ageing bucket (billing.cfg_arrears_ageing_buckets): days overdue from and to, and the next action.</summary>
public sealed record AgeingBucket(string BucketCode, int DaysFrom, int DaysTo, string Action);

public sealed record BillingAccountOpened(Guid AccountId, string AccountNumber, Guid PayerPartyId, string Currency, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record ArrearsDetected(Guid AccountId, string BucketCode, int DaysOverdue, decimal AmountOverdue, string Currency, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>Entity PremiumInstalment (billing.premium_instalment): one amount due on one date.</summary>
public sealed class Instalment : Entity<Guid>, ITenantScoped
{
    private Instalment()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid ScheduleId { get; private set; }

    public DateOnly DueDate { get; private set; }

    public decimal Amount { get; private set; }

    public InstalmentStatus Status { get; private set; }

    public Guid? InvoiceId { get; private set; }

    public long RowVersion { get; private set; }

    internal static Instalment Create(Guid scheduleId, DateOnly dueDate, decimal amount) =>
        new() { Id = Guid.CreateVersion7(), ScheduleId = scheduleId, DueDate = dueDate, Amount = amount, Status = InstalmentStatus.Scheduled };

    internal void Invoiced(Guid invoiceId)
    {
        Status = InstalmentStatus.Invoiced;
        InvoiceId = invoiceId;
    }

    internal void Cancel() => Status = InstalmentStatus.Cancelled;
}

/// <summary>Entity PremiumSchedule (billing.premium_schedule): the instalments of one policy's premium.</summary>
public sealed class PremiumSchedule : Entity<Guid>, ITenantScoped
{
    private readonly List<Instalment> _instalments = [];

    private PremiumSchedule()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid BillingAccountId { get; private set; }

    public string SourceDomain { get; private set; } = "";

    public Guid PolicyRef { get; private set; }

    public string Frequency { get; private set; } = "";

    public DateOnly StartDate { get; private set; }

    public DateOnly? EndDate { get; private set; }

    public decimal InstalmentAmount { get; private set; }

    public string Currency { get; private set; } = "";

    public long RowVersion { get; private set; }

    public IReadOnlyList<Instalment> Instalments => _instalments;

    /// <summary>The product version billed, carried on the invoices (BIL-002; V0015).</summary>
    public string ProductVersionRef { get; internal set; } = "";

    internal static PremiumSchedule Create(Guid accountId, string sourceDomain, Guid policyRef, BillingFrequency frequency, DateOnly start, DateOnly? end, decimal amount,
        string currency)
    {
        var schedule = new PremiumSchedule
        {
            Id = Guid.CreateVersion7(),
            BillingAccountId = accountId,
            SourceDomain = sourceDomain,
            PolicyRef = policyRef,
            Frequency = frequency.Code,
            StartDate = start,
            EndDate = end,
            InstalmentAmount = amount,
            Currency = currency,
        };
        schedule.Generate(frequency, start);
        return schedule;
    }

    /// <summary>
    /// The instalments from <paramref name="from"/> to the end date, at the frequency. A schedule without an end date
    /// is generated a year ahead; TODO(spec): rolling generation for open-ended schedules is not specified (OQ-058).
    /// </summary>
    internal void Generate(BillingFrequency frequency, DateOnly from)
    {
        var months = 12 / frequency.PeriodsPerYear;
        var last = EndDate ?? from.AddYears(1).AddDays(-1);
        for (var due = StartDate; due <= last; due = due.AddMonths(months))
        {
            if (due >= from && _instalments.All(i => i.DueDate != due || i.Status == InstalmentStatus.Cancelled))
            {
                _instalments.Add(Instalment.Create(Id, due, InstalmentAmount));
            }
        }
    }

    /// <summary>The schedule ends the day before <paramref name="from"/>: instalments not yet invoiced from then are cancelled.</summary>
    internal void End(DateOnly from)
    {
        foreach (var instalment in _instalments.Where(i => i.Status == InstalmentStatus.Scheduled && i.DueDate >= from))
        {
            instalment.Cancel();
        }

        EndDate = from.AddDays(-1) < StartDate ? StartDate : from.AddDays(-1);
    }

    /// <summary>AmendSchedule: instalments not yet invoiced from the effective date are cancelled and regenerated at the new amount.</summary>
    internal void Amend(BillingFrequency frequency, decimal amount, DateOnly effectiveFrom)
    {
        foreach (var instalment in _instalments.Where(i => i.Status == InstalmentStatus.Scheduled && i.DueDate >= effectiveFrom))
        {
            instalment.Cancel();
        }

        InstalmentAmount = amount;
        Generate(frequency, effectiveFrom);
    }
}

/// <summary>Entity ArrearsCase (billing.arrears_case).</summary>
public sealed class ArrearsCase : Entity<Guid>, ITenantScoped
{
    private ArrearsCase()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid BillingAccountId { get; private set; }

    public string BucketCode { get; private set; } = "";

    public int DaysOverdue { get; private set; }

    public decimal AmountOverdue { get; private set; }

    public string Currency { get; private set; } = "";

    public string NextAction { get; private set; } = "";

    /// <summary>OPEN or CLEARED.</summary>
    public string Status { get; private set; } = "OPEN";

    public long RowVersion { get; private set; }

    internal static ArrearsCase Open(Guid accountId, AgeingBucket bucket, int days, decimal amount, string currency) => new()
    {
        Id = Guid.CreateVersion7(), BillingAccountId = accountId, BucketCode = bucket.BucketCode, DaysOverdue = days, AmountOverdue = amount, Currency = currency,
        NextAction = bucket.Action.Length <= 15 ? bucket.Action : bucket.Action[..15],
    };

    internal void Age(AgeingBucket bucket, int days, decimal amount)
    {
        BucketCode = bucket.BucketCode;
        DaysOverdue = days;
        AmountOverdue = amount;
        NextAction = bucket.Action.Length <= 15 ? bucket.Action : bucket.Action[..15];
    }

    internal void Clear() => Status = "CLEARED";
}

/// <summary>
/// The BillingAccount aggregate: a payer's account, its premium schedules and arrears. Obligations come only from
/// authorised policy and premium transactions (BIL-001): schedules are created from the domains' events or by an
/// authorised user. Arrears follow the configured ageing buckets (§13.1).
/// </summary>
public sealed class BillingAccount : AggregateRoot<Guid>
{
    public static readonly StateMachine<AccountStatus, AccountTrigger> Lifecycle = new StateMachine<AccountStatus, AccountTrigger>()
        .Allow(AccountStatus.Active, AccountTrigger.DetectArrears, AccountStatus.InArrears)
        .Allow(AccountStatus.InArrears, AccountTrigger.ArrearsCleared, AccountStatus.Active)
        .Allow(AccountStatus.Active, AccountTrigger.CloseAccount, AccountStatus.Closed);

    private readonly List<PremiumSchedule> _schedules = [];
    private readonly List<ArrearsCase> _arrears = [];

    private BillingAccount()
    {
    }

    public string AccountNumber { get; private set; } = "";

    public Guid PayerPartyId { get; private set; }

    public string Currency { get; private set; } = "";

    public string CollectionMethod { get; private set; } = "";

    public string? MandateRef { get; private set; }

    public AccountStatus Status { get; private set; }

    public IReadOnlyList<PremiumSchedule> Schedules => _schedules;

    public IReadOnlyList<ArrearsCase> Arrears => _arrears;

    public static BillingAccount OpenAccount(Guid id, Guid payerPartyId, string currency, string collectionMethod, string? mandateRef, DateTimeOffset now)
    {
        var account = new BillingAccount
        {
            Id = Guard.NotEmpty(id),
            AccountNumber = "BA-" + id.ToString("N")[..12].ToUpperInvariant(),
            PayerPartyId = Guard.NotEmpty(payerPartyId),
            Currency = Guard.MaxLength(Guard.NotNullOrWhiteSpace(currency), 3),
            CollectionMethod = Guard.MaxLength(Guard.NotNullOrWhiteSpace(collectionMethod), 15),
            MandateRef = mandateRef,
            Status = AccountStatus.Active,
        };
        account.Raise(new BillingAccountOpened(id, account.AccountNumber, payerPartyId, account.Currency, now));
        account.MarkChanged();
        return account;
    }

    /// <summary>Command CreateSchedule (BIL-001): one schedule per policy on the account.</summary>
    public Result<PremiumSchedule> CreateSchedule(string sourceDomain, Guid policyRef, string productVersionRef, BillingFrequency frequency, DateOnly start,
        DateOnly? end, decimal instalmentAmount)
    {
        if (Status == AccountStatus.Closed)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "The billing account is closed.");
        }

        if (instalmentAmount <= 0 || frequency.PeriodsPerYear is not (1 or 2 or 4 or 12) || (end is { } e && e < start))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "A schedule has a positive amount, a frequency of 1, 2, 4 or 12 instalments a year, and an end after its start.");
        }

        if (_schedules.Any(s => s.SourceDomain == sourceDomain && s.PolicyRef == policyRef))
        {
            return Error.Conflict(BillingErrorCodes.ScheduleExists, "The policy already has a premium schedule on this account.");
        }

        var schedule = PremiumSchedule.Create(Id, sourceDomain, policyRef, frequency, start, end, instalmentAmount, Currency);
        schedule.ProductVersionRef = productVersionRef;
        _schedules.Add(schedule);
        MarkChanged();
        return schedule;
    }

    public Result AmendSchedule(Guid scheduleId, BillingFrequency frequency, decimal instalmentAmount, DateOnly effectiveFrom)
    {
        if (_schedules.SingleOrDefault(s => s.Id == scheduleId) is not { } schedule)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The account has no such schedule.");
        }

        if (instalmentAmount <= 0)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "An instalment amount is positive.");
        }

        schedule.Amend(frequency, instalmentAmount, effectiveFrom);
        MarkChanged();
        return Result.Success();
    }

    /// <summary>The scheduled instalments due on or before the day, to invoice.</summary>
    public IEnumerable<(PremiumSchedule Schedule, Instalment Instalment)> DueInstalments(DateOnly day) =>
        _schedules.SelectMany(s => s.Instalments.Where(i => i.Status == InstalmentStatus.Scheduled && i.DueDate <= day).Select(i => (s, i)));

    /// <summary>A policy's schedule on this account.</summary>
    public PremiumSchedule? ScheduleFor(string sourceDomain, Guid policyRef) => _schedules.SingleOrDefault(s => s.SourceDomain == sourceDomain && s.PolicyRef == policyRef);

    /// <summary>A cancelled policy's schedule stops from the cancellation date: nothing more is billed.</summary>
    public Result EndSchedule(Guid scheduleId, DateOnly from)
    {
        if (_schedules.SingleOrDefault(s => s.Id == scheduleId) is not { } schedule)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The account has no such schedule.");
        }

        schedule.End(from);
        MarkChanged();
        return Result.Success();
    }

    public void MarkInvoiced(Instalment instalment, Guid invoiceId)
    {
        instalment.Invoiced(invoiceId);
        MarkChanged();
    }

    /// <summary>
    /// Command DetectArrears: the oldest overdue amount decides the bucket. No overdue amount clears the arrears and
    /// returns the account to Active (transition "Arrears cleared").
    /// </summary>
    public Result DetectArrears(int daysOverdue, decimal amountOverdue, IReadOnlyList<AgeingBucket> buckets, DateTimeOffset now)
    {
        var open = _arrears.SingleOrDefault(a => a.Status == "OPEN");
        if (amountOverdue <= 0 || daysOverdue <= 0)
        {
            if (open is null)
            {
                return Result.Success();
            }

            open.Clear();
            MarkChanged();
            return Fire(AccountTrigger.ArrearsCleared);
        }

        if (buckets.FirstOrDefault(b => daysOverdue >= b.DaysFrom && daysOverdue <= b.DaysTo) is not { } bucket)
        {
            return Error.Validation(BillingErrorCodes.NoAgeingBucket, $"No arrears ageing bucket covers {daysOverdue} days overdue.");
        }

        var changed = open is null || open.BucketCode != bucket.BucketCode;
        if (open is null)
        {
            _arrears.Add(ArrearsCase.Open(Id, bucket, daysOverdue, amountOverdue, Currency));
            if (Status == AccountStatus.Active)
            {
                var result = Fire(AccountTrigger.DetectArrears);
                if (result.IsFailure)
                {
                    return result;
                }
            }
        }
        else
        {
            open.Age(bucket, daysOverdue, amountOverdue);
        }

        if (changed)
        {
            Raise(new ArrearsDetected(Id, bucket.BucketCode, daysOverdue, amountOverdue, Currency, now));
        }

        MarkChanged();
        return Result.Success();
    }

    public Result CloseAccount(bool hasOpenInvoices)
    {
        if (hasOpenInvoices)
        {
            return Error.Conflict(BillingErrorCodes.OpenInvoices, "The account has invoices that are not settled.");
        }

        foreach (var instalment in _schedules.SelectMany(s => s.Instalments).Where(i => i.Status == InstalmentStatus.Scheduled))
        {
            instalment.Cancel();
        }

        return Fire(AccountTrigger.CloseAccount);
    }

    private Result Fire(AccountTrigger trigger)
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

public static class BillingErrorCodes
{
    [ErrorCode(409, "error.billing.schedule_exists")]
    public const string ScheduleExists = "PREMIUM_SCHEDULE_EXISTS";

    [ErrorCode(422, "error.billing.no_ageing_bucket")]
    public const string NoAgeingBucket = "ARREARS_BUCKET_MISSING";

    [ErrorCode(409, "error.billing.open_invoices")]
    public const string OpenInvoices = "ACCOUNT_HAS_OPEN_INVOICES";

    [ErrorCode(409, "error.billing.invoice_immutable")]
    public const string InvoiceImmutable = "INVOICE_IMMUTABLE";

    [ErrorCode(422, "error.billing.credit_exceeds_invoice")]
    public const string CreditExceedsInvoice = "CREDIT_EXCEEDS_INVOICE";

    [ErrorCode(422, "error.billing.code_not_in_force")]
    public const string CodeNotInForce = "BILLING_CODE_NOT_IN_FORCE";
}
