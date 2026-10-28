/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Billing
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

public sealed record ArrearsDetected(Guid AccountId, string BucketCode, int DaysOverdue, decimal AmountOverdue, string Currency, DateTimeOffset OccurredAt,
    string? AccountNumber = null, Guid? PayerPartyId = null, string? NextAction = null, string? ProductCode = null, string? OrgNodeCode = null) : IDomainEvent;

/// <summary>An open arrears case aged past every ageing bucket in force: it stays in its bucket until one covers its days (request 46).</summary>
public sealed record ArrearsAgeingBlocked(Guid AccountId, string BucketCode, int DaysOverdue, DateTimeOffset OccurredAt) : IDomainEvent;

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

    /// <summary>
    /// The annual premium the instalments come from, when the schedule is a share of one (a General policy): its instalments
    /// then total exactly their share of it, the last taking the rounding. Null for a schedule of a fixed instalment.
    /// </summary>
    public decimal? AnnualPremium { get; private set; }

    /// <summary>The instalment loading in per cent the policy was quoted with, kept for its amendments; AnnualPremium already includes it. Null when none.</summary>
    public decimal? LoadingPct { get; private set; }

    /// <summary>Keeps the policy's instalment loading for its later amendments.</summary>
    public void UseLoading(decimal? loadingPct) => LoadingPct = loadingPct is > 0 ? loadingPct : null;

    public string Currency { get; private set; } = "";

    public long RowVersion { get; private set; }

    public IReadOnlyList<Instalment> Instalments => _instalments;

    /// <summary>The product version billed, carried on the invoices (BIL-002; V0015).</summary>
    public string ProductVersionRef { get; internal set; } = "";

    /// <summary>
    /// The product and organisation node of the policy or membership billed, carried on the invoices: they scope the
    /// schedule, its invoices and its account (F103, addenda/access-scopes.md §1). A schedule created by hand has neither.
    /// </summary>
    public string? ProductCode { get; private set; }

    public string? OrgNodeCode { get; private set; }

    /// <summary>The product the schedule's membership is now on (addenda/health-plan-products.md); its invoices from now on carry it.</summary>
    public void UseProduct(string productCode) => ProductCode = Guard.MaxLength(productCode, 15);

    public void ScopeTo(string? productCode, string? orgNodeCode)
    {
        ProductCode = productCode is null ? null : Guard.MaxLength(productCode, 15);
        OrgNodeCode = orgNodeCode is null ? null : Guard.MaxLength(orgNodeCode, 15);
    }

    internal static PremiumSchedule Create(Guid accountId, string sourceDomain, Guid policyRef, BillingFrequency frequency, DateOnly start, DateOnly? end, decimal amount,
        string currency, decimal? annualPremium)
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
            AnnualPremium = annualPremium,
            Currency = currency,
        };
        schedule.Generate(frequency, start);
        return schedule;
    }

    /// <summary>
    /// The instalments from <paramref name="from"/> to the end date, at the frequency. A schedule without an end date
    /// is generated a year ahead; TODO(spec): rolling generation for open-ended schedules is not specified (OQ-058).
    /// With an annual premium, the instalments generated total exactly their share of it (the periods they cover over the
    /// periods in a year, to the cent): the last takes the rounding, so twelve monthly instalments of 3 050.00 are eleven
    /// of 254.17 and one of 254.13 (Ade, 28 October 2026).
    /// </summary>
    internal void Generate(BillingFrequency frequency, DateOnly from)
    {
        var months = 12 / frequency.PeriodsPerYear;
        var last = EndDate ?? from.AddYears(1).AddDays(-1);
        var dueDates = new List<DateOnly>();
        for (var due = StartDate; due <= last; due = due.AddMonths(months))
        {
            if (due >= from && _instalments.All(i => i.DueDate != due || i.Status == InstalmentStatus.Cancelled))
            {
                dueDates.Add(due);
            }
        }

        var lastAmount = AnnualPremium is { } annual && dueDates.Count > 0
            ? Math.Round(annual * dueDates.Count / frequency.PeriodsPerYear, 2, MidpointRounding.AwayFromZero) - InstalmentAmount * (dueDates.Count - 1)
            : InstalmentAmount;
        for (var i = 0; i < dueDates.Count; i++)
        {
            _instalments.Add(Instalment.Create(Id, dueDates[i], i == dueDates.Count - 1 && lastAmount > 0 ? lastAmount : InstalmentAmount));
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

    /// <summary>An ended schedule resumes from a date (a reinstated policy) to its new end: instalments are generated again from then.</summary>
    internal void Resume(BillingFrequency frequency, DateOnly from, DateOnly? end)
    {
        EndDate = end;
        Generate(frequency, from);
    }

    /// <summary>
    /// AmendSchedule: instalments not yet invoiced from the effective date are cancelled and regenerated at the new amount
    /// (and, for a share of an annual premium, the new annual premium, whose share they then total).
    /// </summary>
    internal void Amend(BillingFrequency frequency, decimal amount, DateOnly effectiveFrom, decimal? annualPremium)
    {
        foreach (var instalment in _instalments.Where(i => i.Status == InstalmentStatus.Scheduled && i.DueDate >= effectiveFrom))
        {
            instalment.Cancel();
        }

        InstalmentAmount = amount;
        AnnualPremium = annualPremium ?? AnnualPremium;
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
            AccountNumber = References.Of("BA-", id, 15),
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
    /// <param name="annualPremium">The annual premium the instalments share, when they are a share of one: they then total it exactly.</param>
    public Result<PremiumSchedule> CreateSchedule(string sourceDomain, Guid policyRef, string productVersionRef, BillingFrequency frequency, DateOnly start,
        DateOnly? end, decimal instalmentAmount, decimal? annualPremium = null)
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

        var schedule = PremiumSchedule.Create(Id, sourceDomain, policyRef, frequency, start, end, instalmentAmount, Currency, annualPremium);
        schedule.ProductVersionRef = productVersionRef;
        _schedules.Add(schedule);
        MarkChanged();
        return schedule;
    }

    public Result AmendSchedule(Guid scheduleId, BillingFrequency frequency, decimal instalmentAmount, DateOnly effectiveFrom, decimal? annualPremium = null)
    {
        if (_schedules.SingleOrDefault(s => s.Id == scheduleId) is not { } schedule)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The account has no such schedule.");
        }

        if (instalmentAmount <= 0)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "An instalment amount is positive.");
        }

        schedule.Amend(frequency, instalmentAmount, effectiveFrom, annualPremium);
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

    /// <summary>A policy's ended schedule resumes from a date, to its end date (null: open-ended).</summary>
    public Result ResumeSchedule(Guid scheduleId, BillingFrequency frequency, DateOnly from, DateOnly? end)
    {
        if (_schedules.SingleOrDefault(s => s.Id == scheduleId) is not { } schedule)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The account has no such schedule.");
        }

        if (Status == AccountStatus.Closed || (end is { } e && e < from))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "A schedule resumes on an open account, before its end.");
        }

        schedule.Resume(frequency, from, end);
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
            if (open is null)
            {
                return Error.Validation(BillingErrorCodes.NoAgeingBucket, $"No arrears ageing bucket covers {daysOverdue} days overdue.");
            }

            // Request 46: an open case past every bucket stays in its bucket with its true days overdue, and says so once,
            // on the day it leaves its bucket's range, so the delivery that ages it on is parked until a bucket covers it.
            var within = buckets.FirstOrDefault(b => b.BucketCode == open.BucketCode) is not { } current || open.DaysOverdue <= current.DaysTo;
            open.Age(new AgeingBucket(open.BucketCode, 0, 0, open.NextAction), daysOverdue, amountOverdue);
            if (within)
            {
                Raise(new ArrearsAgeingBlocked(Id, open.BucketCode, daysOverdue, now));
            }

            MarkChanged();
            return Result.Success();
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
            Raise(new ArrearsDetected(Id, bucket.BucketCode, daysOverdue, amountOverdue, Currency, now, AccountNumber, PayerPartyId, bucket.Action));
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
    [ErrorCode(409, "error.billing.approval_subject_changed")]
    public const string ApprovalSubjectChanged = "REFUND_SUBJECT_CHANGED";

    [ErrorCode(422, "error.billing.bank_account_not_payable")]
    public const string BankAccountNotPayable = "REFUND_ACCOUNT_NOT_PAYABLE";

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
