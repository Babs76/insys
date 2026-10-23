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
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Domain.Expressions;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Finance.Application;
using Insys.Finance.Domain.Ledger;
using Insys.Finance.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;

namespace Insys.Finance.Infrastructure.Persistence;

internal sealed class JournalRepository(FinanceDbContext db) : IJournalRepository
{
    public Task<Journal?> Get(Guid id, CancellationToken cancellationToken) =>
        db.Journals.Include(j => j.Lines).SingleOrDefaultAsync(j => j.Id == id, cancellationToken);

    public Task<bool> IsReversed(Guid journalId, CancellationToken cancellationToken) => db.Journals.AnyAsync(j => j.ReversesJournalId == journalId, cancellationToken);

    public Task<Journal?> BySourceEvent(Guid sourceEventId, CancellationToken cancellationToken) =>
        db.Journals.AsNoTracking().Include(j => j.Lines).FirstOrDefaultAsync(j => j.SourceEventId == sourceEventId, cancellationToken);

    public async Task<IReadOnlyList<Journal>> ForBusinessRef(string businessRefType, Guid businessRefId, CancellationToken cancellationToken) =>
        await db.Journals.AsNoTracking().Include(j => j.Lines)
            .Where(j => j.Lines.Any(l => l.BusinessRefType == businessRefType && l.BusinessRefId == businessRefId))
            .OrderBy(j => j.PostedAt).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<(string Account, string Currency, decimal Debit, decimal Credit)>> TrialBalance(string periodCode, CancellationToken cancellationToken) =>
        (await db.Set<SubledgerEntry>().AsNoTracking()
            .Where(e => db.Journals.Any(j => j.Id == e.JournalId && j.PeriodCode == periodCode))
            .GroupBy(e => new { e.AccountCode, e.Currency })
            .Select(g => new { g.Key.AccountCode, g.Key.Currency, Debit = g.Sum(e => e.Debit), Credit = g.Sum(e => e.Credit) })
            .OrderBy(g => g.AccountCode).ToListAsync(cancellationToken))
        .Select(g => (g.AccountCode, g.Currency, g.Debit, g.Credit)).ToList();

    public void Add(Journal journal) => db.Journals.Add(journal);
}

internal sealed class AccountingPeriods(FinanceDbContext db) : IAccountingPeriods
{
    /// <summary>An open period covering the date first (at most one, V0012), else any period covering it.</summary>
    public async Task<AccountingPeriod?> Covering(DateOnly date, CancellationToken cancellationToken) =>
        await db.Periods.Where(p => p.StartDate <= date && p.EndDate >= date).OrderBy(p => p.Status == PeriodStatus.Open ? 0 : 1).ThenBy(p => p.StartDate)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<AccountingPeriod?> ByCode(string periodCode, CancellationToken cancellationToken) =>
        db.Periods.SingleOrDefaultAsync(p => p.PeriodCode == periodCode, cancellationToken);

    public Task<AccountingPeriod?> Get(Guid id, CancellationToken cancellationToken) => db.Periods.SingleOrDefaultAsync(p => p.Id == id, cancellationToken);

    public async Task<IReadOnlyList<AccountingPeriod>> All(CancellationToken cancellationToken) =>
        await db.Periods.AsNoTracking().OrderBy(p => p.StartDate).ToListAsync(cancellationToken);

    public void Add(AccountingPeriod period) => db.Periods.Add(period);
}

internal sealed class PostingExceptions(FinanceDbContext db) : IPostingExceptions
{
    public Task<PostingException?> Get(Guid id, CancellationToken cancellationToken) => db.PostingExceptions.SingleOrDefaultAsync(e => e.Id == id, cancellationToken);

    public async Task<IReadOnlyList<PostingException>> List(string? status, int limit, CancellationToken cancellationToken) =>
        await db.PostingExceptions.AsNoTracking().Where(e => status == null || e.Status == status).OrderBy(e => e.Id).Take(limit).ToListAsync(cancellationToken);

    public void Add(PostingException exception) => db.PostingExceptions.Add(exception);
}

/// <summary>Posting rules, accounts, rates and the calendar in force, from the Finance module's static data.</summary>
internal sealed class PostingConfiguration(FinanceDbContext db) : IPostingConfiguration
{
    public async Task<Result<IReadOnlyList<PostingRule>>> Rules(DateOnly day, CancellationToken cancellationToken)
    {
        var rules = new List<PostingRule>();
        foreach (var row in await InForce<PostingRules>(day).ToListAsync(cancellationToken))
        {
            Expression? condition = null;
            if (!string.IsNullOrWhiteSpace(row.Conditions))
            {
                var parsed = Expression.Parse(row.Conditions);
                if (parsed.IsFailure)
                {
                    return Error.Validation(parsed.Error.Code, $"Posting rule {row.RuleCode} v{row.Version}: {parsed.Error.Message}");
                }

                condition = parsed.Value;
            }

            rules.Add(new PostingRule(row.RuleCode, row.Version, row.TransactionType, condition, row.DebitAccount, row.CreditAccount));
        }

        return rules;
    }

    public Task<bool> AccountUsable(string accountCode, string currency, DateOnly day, CancellationToken cancellationToken) =>
        InForce<ChartOfAccountsSubledger>(day).AnyAsync(a => a.AccountCode == accountCode && a.Status == "ACTIVE" && (a.Currency == null || a.Currency == currency),
            cancellationToken);

    public async Task<decimal?> Rate(string from, string to, DateOnly day, CancellationToken cancellationToken) =>
        await InForce<ExchangeRatesAndRateTypes>(day).Where(r => r.RateType == "SPOT" && r.FromCurrency == from && r.ToCurrency == to && r.RateDate <= day)
            .OrderByDescending(r => r.RateDate).ThenByDescending(r => r.TenantId != null).Select(r => (decimal?)r.Rate).FirstOrDefaultAsync(cancellationToken);

    public async Task<(DateOnly Start, DateOnly End)?> CalendarPeriod(string periodCode, DateOnly day, CancellationToken cancellationToken) =>
        await db.Set<AccountingCalendarAndPeriods>().AsNoTracking().InForce(Lifecycle<AccountingCalendarAndPeriods>(), day).Where(p => p.PeriodCode == periodCode)
            .Select(p => new { p.StartDate, p.EndDate }).FirstOrDefaultAsync(cancellationToken) is { } period
            ? (period.StartDate, period.EndDate)
            : null;

    private IQueryable<T> InForce<T>(DateOnly day) where T : StaticRecord => db.Set<T>().AsNoTracking().InForce(Lifecycle<T>(), day);

    private static StaticLifecycle Lifecycle<T>() => FinanceStaticData.Descriptors.Single(d => d.EntityType == typeof(T)).Lifecycle;
}

internal sealed class FinanceEventMapper : IDomainEventMapper
{
    public IIntegrationEvent? Map(IDomainEvent domainEvent) => domainEvent switch
    {
        SubledgerEntryPosted e => new Contracts.SubledgerEntryPosted(e.JournalId, e.JournalNumber, e.AccountingDate, e.TransactionType, e.Amount, e.Currency,
            e.BusinessRefType, e.BusinessRefId, e.PostingRuleCode, e.PostingRuleVersion),
        JournalReversed e => new Contracts.JournalReversed(e.JournalId, e.ReversalJournalId, e.AccountingDate),
        PostingFailed e => new Contracts.PostingFailed(e.ExceptionId, e.SourceEventId, e.EventType, e.Reason),
        AccountingPeriodClosed e => new Contracts.AccountingPeriodClosed(e.PeriodId, e.PeriodCode, e.Final),
        PeriodReopenRequested e => new BuildingBlocks.Contracts.Approvals.ApprovalRequested(Contracts.FinanceApprovals.PeriodBusinessRefType, e.PeriodId,
            Contracts.FinanceApprovals.ReopenApprovalType, e.RequestedBy, null, null, $"Reopen accounting period {e.PeriodCode}"),
        _ => null,
    };
}
