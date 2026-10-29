/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Workflow
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
using System.Globalization;
using Insys.BuildingBlocks.Application;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Workflow.Application;
using Insys.Workflow.Contracts;
using Insys.Workflow.Domain.Cases;
using Insys.Workflow.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Insys.Workflow.Infrastructure.Persistence;

internal sealed class CaseRepository(WorkflowDbContext db) : ICaseRepository
{
    private static readonly CaseStatus[] Ended = [CaseStatus.Closed, CaseStatus.Withdrawn];

    public Task<ServiceCase?> Get(Guid id, CancellationToken cancellationToken) =>
        db.Cases.Include(c => c.Links).SingleOrDefaultAsync(c => c.Id == id, cancellationToken);

    /// <summary>One counter row per tenant, created or incremented in the command's own transaction, so numbers are never reused.</summary>
    public async Task<string> NextCaseNumber(CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction()
                              ?? throw new InvalidOperationException("Case numbers are taken inside the command's transaction.");
        command.CommandText = """
            INSERT INTO workflow.case_number_counter (tenant_id, last_number) VALUES (@t, 1)
            ON CONFLICT (tenant_id) DO UPDATE SET last_number = workflow.case_number_counter.last_number + 1
            RETURNING last_number
            """;
        var tenant = command.CreateParameter();
        tenant.ParameterName = "t";
        tenant.Value = db.CurrentTenantId;
        command.Parameters.Add(tenant);
        var number = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        return Format(number);
    }

    /// <summary>"CASE-" and the tenant's sequence, six digits or more: CASE-000001.</summary>
    public static string Format(long number) => "CASE-" + number.ToString("000000", CultureInfo.InvariantCulture);

    public void Add(ServiceCase serviceCase) => db.Cases.Add(serviceCase);

    public void Remove(ServiceCase serviceCase) => db.Cases.Remove(serviceCase);

    public Task<CaseRedress?> Redress(Guid id, CancellationToken cancellationToken) => db.CaseRedress.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);

    public void Add(CaseRedress redress) => db.CaseRedress.Add(redress);

    public Task<int> CountHistory(Guid caseId, string eventType, CancellationToken cancellationToken) =>
        db.CaseHistory.CountAsync(h => h.CaseId == caseId && h.EventType == eventType, cancellationToken);

    public async Task<IReadOnlyList<Guid>> Overdue(DateTimeOffset now, int limit, CancellationToken cancellationToken) =>
        await db.Cases.AsNoTracking()
            .Where(c => !Ended.Contains(c.Status) && c.ExpectedBy != null && c.ExpectedBy <= now
                && !db.CaseHistory.Any(h => h.CaseId == c.Id && h.EventType == CaseEventTypes.Updated && h.Detail!.StartsWith("OVERDUE ") && h.OccurredAt >= c.ExpectedBy))
            .OrderBy(c => c.ExpectedBy).Select(c => c.Id).Take(limit).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> DueForDisposal(DateOnly today, int limit, CancellationToken cancellationToken) =>
        await db.Cases.AsNoTracking().Where(c => Ended.Contains(c.Status) && c.RetainUntil != null && c.RetainUntil < today)
            .OrderBy(c => c.RetainUntil).Select(c => c.Id).Take(limit).ToListAsync(cancellationToken);
}

/// <summary>The case configuration in force (workflow static data); a tenant's own reference row before a global one.</summary>
internal sealed class CaseConfiguration(WorkflowDbContext db) : ICaseConfiguration
{
    public async Task<CaseTypeConfig?> Type(string caseTypeCode, DateOnly day, CancellationToken cancellationToken) =>
        await InForce<CaseTypes>(day).Where(t => t.CaseTypeCode == caseTypeCode).FirstOrDefaultAsync(cancellationToken) is { } t
            ? new CaseTypeConfig(t.CaseTypeCode, t.Family, t.ProcessCode, t.DefaultQueueCode, t.LinkTypes, t.PortalAudiences ?? [], t.RequiresCategory,
                t.ReviewCaseTypeCode, t.AppealRoute, t.OmbudSchemeCodes ?? [])
            : null;

    public async Task<IReadOnlyList<CaseTypeConfig>> ClaimDisputeTypes(DateOnly day, CancellationToken cancellationToken) =>
        (await InForce<CaseTypes>(day).Where(t => t.Family == "DISPUTE" && t.LinkTypes.Contains("CLAIM")).OrderBy(t => t.CaseTypeCode).ToListAsync(cancellationToken))
        .Select(t => new CaseTypeConfig(t.CaseTypeCode, t.Family, t.ProcessCode, t.DefaultQueueCode, t.LinkTypes, t.PortalAudiences ?? [], t.RequiresCategory,
            t.ReviewCaseTypeCode, t.AppealRoute, t.OmbudSchemeCodes ?? [])).ToList();

    public async Task<IReadOnlyList<CaseTimeline>> Timelines(string caseTypeCode, DateOnly day, CancellationToken cancellationToken) =>
        (await InForce<CaseTimelines>(day).Where(t => t.CaseTypeCode == caseTypeCode).ToListAsync(cancellationToken))
        .GroupBy(t => t.Step).Select(g => g.OrderByDescending(t => t.EffectiveFrom).First())
        .Select(t => new CaseTimeline(t.Step, t.Duration, t.Basis, t.CalendarCode, t.ExtensionAllowed, t.MaxExtension)).ToList();

    public Task<string?> QueryWindow(string caseTypeCode, DateOnly day, CancellationToken cancellationToken) =>
        InForce<CaseReportability>(day).Where(r => r.CaseTypeCode == caseTypeCode).Select(r => (string?)r.QueryWindow).FirstOrDefaultAsync(cancellationToken);

    public Task<string?> RetainFor(string family, DateOnly day, CancellationToken cancellationToken) =>
        InForce<CaseRetention>(day).Where(r => r.Family == family).Select(r => (string?)r.RetainFor).FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<OmbudSchemeInfo>> OmbudSchemes(IReadOnlyCollection<string> schemeCodes, DateOnly day, CancellationToken cancellationToken)
    {
        if (schemeCodes.Count == 0)
        {
            return [];
        }

        var codes = schemeCodes.ToArray();
        var rows = await InForce<OmbudSchemes>(day).Where(s => codes.Contains(s.SchemeCode)).ToListAsync(cancellationToken);
        return rows.GroupBy(r => r.SchemeCode).Select(g => g.OrderByDescending(r => r.TenantId != null).First())
            .OrderBy(s => Array.IndexOf(codes, s.SchemeCode)).Select(s => new OmbudSchemeInfo(s.SchemeCode, s.Name, s.Contact, s.ReferralInformation)).ToList();
    }

    public Task<bool> CategoryExists(string categoryCode, DateOnly day, CancellationToken cancellationToken) =>
        InForce<CaseCategories>(day).AnyAsync(c => c.CategoryCode == categoryCode, cancellationToken);

    /// <summary>The PPR category of each category code in force (a tenant's own first).</summary>
    public async Task<Dictionary<string, string>> PprCategories(DateOnly day, CancellationToken cancellationToken) =>
        (await InForce<CaseCategories>(day).ToListAsync(cancellationToken)).GroupBy(c => c.CategoryCode)
        .ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.TenantId != null).First().PprCategory, StringComparer.Ordinal);

    private IQueryable<T> InForce<T>(DateOnly day)
        where T : StaticRecord =>
        db.Set<T>().AsNoTracking().InForce(WorkflowStaticData.Descriptors.Single(d => d.EntityType == typeof(T)).Lifecycle, day);
}

/// <summary>Staff case reads (§11): within the caller's F103 scope (a case with no scoped record is tenant-wide), Health-linked ones only with medical_claim.read.</summary>
internal sealed class CaseQueries(WorkflowDbContext db, IExecutionContext execution, CaseAccess access, CaseConfiguration configuration, IBusinessDate businessDate,
    IClock clock, Documents.Contracts.IRecordHolds holds) : ICaseQueries
{
    private static readonly CaseStatus[] Ended = [CaseStatus.Closed, CaseStatus.Withdrawn];

    public async Task<IReadOnlyList<CaseSummaryView>> List(CaseFilter filter, Guid? afterId, int limit, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var query = Visible();
        query = filter.Family is { } family ? query.Where(c => c.Family == family) : query;
        query = filter.CaseTypeCode is { } type ? query.Where(c => c.CaseTypeCode == type) : query;
        query = filter.Status is { } status ? query.Where(c => c.Status == status) : query;
        query = filter.CategoryCode is { } category ? query.Where(c => c.CategoryCode == category) : query;
        query = filter.Overdue switch
        {
            true => query.Where(c => !Ended.Contains(c.Status) && c.ExpectedBy != null && c.ExpectedBy < now),
            false => query.Where(c => Ended.Contains(c.Status) || c.ExpectedBy == null || c.ExpectedBy >= now),
            _ => query,
        };
        if (filter.LinkType is not null || filter.LinkId is not null)
        {
            query = query.Where(c => c.Links.Any(l => (filter.LinkType == null || l.LinkType == filter.LinkType) && (filter.LinkId == null || l.RecordId == filter.LinkId)));
        }

        query = afterId is { } after ? query.Where(c => c.Id.CompareTo(after) > 0) : query;
        return (await query.OrderBy(c => c.Id).Take(limit).ToListAsync(cancellationToken))
            .Select(c => new CaseSummaryView(c.Id, c.CaseNumber, c.CaseTypeCode, c.Family, c.Status, c.Outcome, c.CategoryCode, c.ReceivedAt, c.ExpectedBy,
                IsOverdue(c, now), c.ComplainantPartyId, c.ParentCaseId, c.RowVersion)).ToList();
    }

    public async Task<CaseView?> Get(Guid id, CancellationToken cancellationToken)
    {
        var c = await db.Cases.AsNoTracking().Include(x => x.Links).SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (c is null || (CaseAccess.IsHealth(c) && !access.MayReadHealth) || !access.InScope(c))
        {
            return null;
        }

        var history = await db.CaseHistory.AsNoTracking().Where(h => h.CaseId == id).OrderBy(h => h.OccurredAt).ThenBy(h => h.Id).ToListAsync(cancellationToken);
        var messages = await db.CaseMessages.AsNoTracking().Where(m => m.CaseId == id).OrderBy(m => m.SentAt).ThenBy(m => m.Id).ToListAsync(cancellationToken);
        var redress = await db.CaseRedress.AsNoTracking().Where(r => r.CaseId == id).OrderBy(r => r.Id).ToListAsync(cancellationToken);
        // Held while a record hold is in force on the case or one of its documents (D-34; the case's own flag is retired, V0068).
        var held = (await holds.Holds([c.Id], cancellationToken)).Count > 0;
        return new CaseView(c.Id, c.CaseNumber, c.CaseTypeCode, c.Family, c.Status, c.Outcome, c.ReceivedAt, c.ReceivedChannel, c.ComplainantPartyId, c.ComplainantName,
            c.ComplainantContact, c.OnBehalfOfPartyId, c.RaisedByAudience, c.RaisedByUserId, c.RaisedByIntermediaryCode, c.RaisedByProviderId, c.SharedWithBroker,
            c.CategoryCode, c.Summary, c.UpheldAtReceipt, c.Reportable, c.DecisionReasons, c.DecidedBy, c.DecidedAt, c.ParentCaseId, c.ProcessInstanceId, c.ProductCode,
            c.OrgNodeCode, c.AcknowledgedAt, c.ExpectedBy, IsOverdue(c, clock.UtcNow), c.ClosedAt, c.RetainUntil, held, c.OmbudSchemeCode, c.OmbudReference,
            c.OmbudReferredAt, c.OmbudOutcome, c.OmbudOutcomeAt,
            c.Links.OrderBy(l => l.AddedAt).Select(l => new CaseLinkView(l.LinkType, l.RecordId, l.Reference, l.AddedAt)).ToList(),
            history.Select(h => new CaseHistoryView(h.EventType, h.OccurredAt, h.Actor, h.Detail)).ToList(),
            messages.Select(m => new CaseMessageView(m.Id, m.Direction, m.AuthorAudience, m.Body, m.SentAt)).ToList(),
            redress.Select(r => CaseRedressView.Of(r)).ToList(), c.RowVersion,
            // The step a timeline extension applies to while the case is open, and how many extensions it has had.
            c.IsOpen ? c.CurrentStep : null, history.Count(h => h.EventType == CaseEventTypes.Extended));
    }

    /// <summary>§8: the cases dashboard. Complaint figures count every complaint the caller may see; redress paid is per currency.</summary>
    public async Task<DashboardSummary> Summary(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var cases = await Visible().Select(c => new { c.Id, c.Family, c.Status, c.Outcome, c.ExpectedBy, c.OmbudReferredAt }).ToListAsync(cancellationToken);
        var open = cases.Where(c => !Ended.Contains(c.Status)).ToList();
        var complaints = cases.Where(c => c.Family == CaseFamilies.Complaint).ToList();
        var visibleIds = Visible().Select(c => c.Id);
        var paid = await db.CaseRedress.AsNoTracking().Where(r => r.Status == RedressStatus.Paid && visibleIds.Contains(r.CaseId))
            .GroupBy(r => r.Currency).Select(g => new { Currency = g.Key, Amount = g.Sum(r => r.Amount) }).ToListAsync(cancellationToken);
        var upheld = complaints.Where(c => c.Outcome is CaseOutcomes.Upheld or CaseOutcomes.PartlyUpheld).ToList();
        return new DashboardSummary(now,
        [
            new DashboardFigure(CaseFigures.CasesOpen, open.Count,
                By: open.GroupBy(c => $"{c.Family}:{UpperCaseEnumConverter<CaseStatus>.ToCode(c.Status)}").OrderBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g => new FigurePart(g.Key, g.Count())).ToList()),
            new DashboardFigure(CaseFigures.CasesOverdue, open.Count(c => c.ExpectedBy is { } due && due < now)),
            new DashboardFigure(CaseFigures.ComplaintsReceived, complaints.Count),
            new DashboardFigure(CaseFigures.ComplaintsUpheld, upheld.Count,
                By: upheld.GroupBy(c => c.Outcome!).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new FigurePart(g.Key, g.Count())).ToList()),
            new DashboardFigure(CaseFigures.ComplaintsRejected, complaints.Count(c => c.Outcome == CaseOutcomes.Rejected)),
            new DashboardFigure(CaseFigures.ComplaintsReferredToOmbud, complaints.Count(c => c.OmbudReferredAt is not null)),
            new DashboardFigure(CaseFigures.RedressPaid, Amounts: paid.OrderBy(p => p.Currency, StringComparer.Ordinal).Select(p => new MoneyAmount(p.Currency, p.Amount)).ToList()),
        ]);
    }

    /// <summary>§8 (PPR 18.8.3): complaints received in the period (business dates, ADR-025), per category.</summary>
    public async Task<ComplaintsReport> ComplaintsReport(DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var zone = businessDate.Zone;
        var start = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(from.ToDateTime(TimeOnly.MinValue), zone), TimeSpan.Zero);
        var end = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(to.AddDays(1).ToDateTime(TimeOnly.MinValue), zone), TimeSpan.Zero);
        var complaints = await Visible().Where(c => c.Family == CaseFamilies.Complaint && c.ReceivedAt >= start && c.ReceivedAt < end)
            .Select(c => new { c.Id, c.CategoryCode, c.Status, c.Outcome, c.DecisionReasons, c.OmbudReferredAt, c.OmbudOutcome, c.Reportable })
            .ToListAsync(cancellationToken);
        var ids = complaints.Select(c => c.Id).ToList();
        var escalated = (await db.CaseHistory.AsNoTracking().Where(h => ids.Contains(h.CaseId) && h.EventType == CaseEventTypes.Escalated).Select(h => h.CaseId)
            .Distinct().ToListAsync(cancellationToken)).ToHashSet();
        var late = (await db.CaseHistory.AsNoTracking().Where(h => ids.Contains(h.CaseId) && h.EventType == CaseEventTypes.Updated && h.Detail!.StartsWith("OVERDUE "))
            .Select(h => h.CaseId).Distinct().ToListAsync(cancellationToken)).ToHashSet();
        var redress = await db.CaseRedress.AsNoTracking().Where(r => ids.Contains(r.CaseId) && (r.Status == RedressStatus.Approved || r.Status == RedressStatus.Paid))
            .Select(r => new { r.CaseId, r.RedressType, r.Amount, r.Currency }).ToListAsync(cancellationToken);
        var ppr = await configuration.PprCategories(businessDate.Today, cancellationToken);
        var categories = complaints.GroupBy(c => c.CategoryCode ?? "").OrderBy(g => g.Key, StringComparer.Ordinal).Select(g =>
        {
            var inCategory = g.Select(c => c.Id).ToHashSet();
            List<CurrencyAmount> Money(string type) => redress.Where(r => inCategory.Contains(r.CaseId) && r.RedressType == type).GroupBy(r => r.Currency)
                .OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => new CurrencyAmount(m.Key, m.Count(), m.Sum(r => r.Amount))).ToList();
            return new ComplaintCategoryFigures(g.Key, ppr.GetValueOrDefault(g.Key), g.Count(), g.Count(c => c.Outcome == CaseOutcomes.Upheld),
                g.Count(c => c.Outcome == CaseOutcomes.PartlyUpheld), g.Count(c => c.Outcome == CaseOutcomes.Rejected),
                g.Where(c => c.Outcome == CaseOutcomes.Rejected && c.DecisionReasons is not null).Select(c => c.DecisionReasons!).ToList(),
                g.Count(c => escalated.Contains(c.Id)), g.Count(c => c.OmbudReferredAt is not null),
                g.Where(c => c.OmbudOutcome is not null).GroupBy(c => c.OmbudOutcome!).OrderBy(o => o.Key, StringComparer.Ordinal)
                    .Select(o => new FigurePart(o.Key, o.Count())).ToList(),
                Money(RedressTypes.Compensation), Money(RedressTypes.Goodwill), g.Count(c => !Ended.Contains(c.Status)), g.Count(c => c.Reportable == true),
                g.Count(c => !late.Contains(c.Id)));
        }).ToList();
        return new ComplaintsReport(from, to, clock.UtcNow, categories);
    }

    private static bool IsOverdue(ServiceCase c, DateTimeOffset now) => c.IsOpen && c.ExpectedBy is { } due && due < now;

    private IQueryable<ServiceCase> Visible()
    {
        var query = db.Cases.AsNoTracking();
        if (!execution.CurrentScope.IsUnrestricted)
        {
            var inScope = db.Cases.AsNoTracking().InScope(execution.CurrentScope, c => c.ProductCode, c => c.OrgNodeCode).Select(c => c.Id);
            query = query.Where(c => (c.ProductCode == null && c.OrgNodeCode == null) || inScope.Contains(c.Id));
        }

        if (!access.MayReadHealth)
        {
            var health = CaseAccess.HealthLinkTypes.ToArray();
            query = query.Where(c => c.RaisedByProviderId == null && !c.Links.Any(l => health.Contains(l.LinkType)));
        }

        return query;
    }
}

/// <summary>Cases in an external user's relationship (§5, OQ-108), in the query so paging stays correct.</summary>
internal sealed class CasePortalQueries(WorkflowDbContext db) : ICasePortalQueries
{
    public async Task<IReadOnlyList<ServiceCase>> List(ExternalSubject who, IReadOnlyCollection<Guid> brokeredPolicies, Guid? afterId, int limit,
        CancellationToken cancellationToken)
    {
        var query = db.Cases.AsNoTracking();
        if (who.Kind == ExternalSubject.Party && who.PartyId is { } party)
        {
            query = query.Where(c => c.ComplainantPartyId == party || c.OnBehalfOfPartyId == party);
        }
        else if (who.Kind == ExternalSubject.Intermediary)
        {
            var codes = who.IntermediaryCodes.ToArray();
            var policies = brokeredPolicies.ToArray();
            query = query.Where(c => (c.RaisedByIntermediaryCode != null && codes.Contains(c.RaisedByIntermediaryCode))
                || (c.SharedWithBroker && c.Links.Any(l => l.LinkType == CaseLinkTypes.Policy && policies.Contains(l.RecordId))));
        }
        else if (who.Kind == ExternalSubject.Provider && who.ProviderId is { } provider)
        {
            query = query.Where(c => c.RaisedByProviderId == provider);
        }
        else
        {
            return [];
        }

        query = afterId is { } after ? query.Where(c => c.Id.CompareTo(after) > 0) : query;
        return await query.OrderBy(c => c.Id).Take(limit).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CaseMessage>> Messages(Guid caseId, CancellationToken cancellationToken) =>
        await db.CaseMessages.AsNoTracking().Where(m => m.CaseId == caseId).OrderBy(m => m.SentAt).ThenBy(m => m.Id).ToListAsync(cancellationToken);
}

/// <summary>
/// ICaseNotices (§7): what a case notice's merge fields need, for Communications, in the current tenant. Read on Workflow's
/// own connection in a transaction of its own (row-level security), never put in an event.
/// </summary>
internal sealed class CaseNotices(WorkflowDbContext db, ICaseRepository cases, ICaseConfiguration configuration, CaseNoticeTexts texts, IBusinessDate businessDate)
    : ICaseNotices
{
    public async Task<CaseNoticeData?> ForNotice(Guid caseId, CancellationToken cancellationToken)
    {
        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        if (await cases.Get(caseId, cancellationToken) is not { } c)
        {
            return null;
        }

        var (review, ombud) = await texts.For(await configuration.Type(c.CaseTypeCode, businessDate.Today, cancellationToken), cancellationToken);
        var data = new CaseNoticeData(c.CaseNumber, c.CaseTypeCode, c.Family, c.ExpectedBy, c.DecisionReasons, review, ombud,
            await texts.RepresentationsDeadline(c, cancellationToken), c.ComplainantPartyId, c.RaisedByIntermediaryCode, c.RaisedByProviderId);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return data;
    }
}

/// <summary>
/// IClaimAppealRoutes (OQ-101, OQ-102): the tenant's dispute case type for claims gives a claim decision notice its appeal
/// route, review options and ombud schemes. A Health claim takes a CMS type where the tenant has one; otherwise, and for
/// Life and General, a PPR_NFO type. Read on Workflow's own connection, in a transaction of its own.
/// </summary>
internal sealed class ClaimAppealRoutes(WorkflowDbContext db, ICaseConfiguration configuration, CaseNoticeTexts texts, IBusinessDate businessDate) : IClaimAppealRoutes
{
    public async Task<ClaimAppealRoute?> For(string domain, CancellationToken cancellationToken)
    {
        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        var types = await configuration.ClaimDisputeTypes(businessDate.Today, cancellationToken);
        var preferred = domain == "HEALTH" ? "CMS" : "PPR_NFO";
        ClaimAppealRoute? route = null;
        if ((types.FirstOrDefault(t => t.AppealRoute == preferred) ?? types.FirstOrDefault()) is { } type)
        {
            var (review, ombud) = await texts.For(type, cancellationToken);
            route = new ClaimAppealRoute(type.AppealRoute, review, ombud);
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return route;
    }
}

internal sealed class CaseEventMapper : IDomainEventMapper
{
    public IIntegrationEvent? Map(IDomainEvent domainEvent) => domainEvent switch
    {
        Domain.Cases.CaseOpened e => new Contracts.CaseOpened(e.CaseId, e.CaseNumber, e.CaseTypeCode, e.Family, e.ComplainantPartyId, Links(e.Links)),
        Domain.Cases.CaseAcknowledged e => new Contracts.CaseAcknowledged(e.CaseId, e.CaseNumber, e.Family, e.ComplainantPartyId, e.ExpectedBy),
        Domain.Cases.CaseUpdated e => new Contracts.CaseUpdated(e.CaseId, e.CaseNumber, e.Family, e.ComplainantPartyId, e.Reason, e.ExpectedBy),
        Domain.Cases.CaseEscalated e => new Contracts.CaseEscalated(e.CaseId, e.CaseNumber, e.Family, e.Level),
        Domain.Cases.CaseDecided e => new Contracts.CaseDecided(e.CaseId, e.CaseNumber, e.CaseTypeCode, e.Family, e.Outcome, e.ComplainantPartyId, Links(e.Links)),
        Domain.Cases.CaseReferredToOmbud e => new Contracts.CaseReferredToOmbud(e.CaseId, e.CaseNumber, e.SchemeCode),
        Domain.Cases.CaseClosed e => new Contracts.CaseClosed(e.CaseId, e.CaseNumber, e.Family, e.Outcome),
        Domain.Cases.CaseReopened e => new Contracts.CaseReopened(e.CaseId, e.CaseNumber, e.Family),
        Domain.Cases.CaseRedressApproved e => new Contracts.CaseRedressApproved(e.RedressId, e.CaseId, e.CaseNumber, e.RedressType, e.Amount, e.Currency, e.PayeePartyId,
            e.BankAccountId),
        RedressApprovalRequested e => new BuildingBlocks.Contracts.Approvals.ApprovalRequested(CaseApprovals.RedressBusinessRefType, e.RedressId,
            CaseApprovals.RedressApprovalType, e.MakerId, e.Amount, e.Currency, $"Redress on case {e.CaseNumber}", e.SubjectHash),
        _ => null,
    };

    private static List<CaseLinkRef> Links(IReadOnlyList<CaseLinkRecord> links) => links.Select(l => new CaseLinkRef(l.LinkType, l.RecordId, l.Reference)).ToList();
}
