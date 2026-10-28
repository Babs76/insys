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
using System.Text.Json;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Domain.Expressions;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Workflow.Application;
using Insys.Workflow.Domain.Approvals;
using Insys.Workflow.Domain.Processes;
using Insys.Workflow.Infrastructure.StaticData;
using Insys.BuildingBlocks.Application.Context;
using Insys.StaticData.Contracts;
using Insys.Tenancy.Contracts;
using Insys.Workflow.Contracts;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Text;

namespace Insys.Workflow.Infrastructure.Persistence;

internal sealed class ApprovalRepository(WorkflowDbContext db) : IApprovalRepository
{
    public Task<Approval?> Get(Guid id, CancellationToken cancellationToken) => db.Approvals.SingleOrDefaultAsync(a => a.Id == id, cancellationToken);

    public Task<Approval?> FindRequested(string businessRefType, Guid businessRefId, CancellationToken cancellationToken) =>
        db.Approvals.SingleOrDefaultAsync(a => a.BusinessRefType == businessRefType && a.BusinessRefId == businessRefId && a.Status == ApprovalStatus.Requested,
            cancellationToken);

    public async Task<IReadOnlyList<Guid>> DueForExpiry(DateTimeOffset now, int limit, CancellationToken cancellationToken) =>
        await db.Approvals.Where(a => a.Status == ApprovalStatus.Requested && a.ExpiresAt != null && a.ExpiresAt <= now).Select(a => a.Id).Take(limit)
            .ToListAsync(cancellationToken);

    public void Add(Approval approval) => db.Approvals.Add(approval);
}

/// <summary>Approvals as listed and read: one about a scoped record is shown only within the caller's scope (F103), otherwise 404.</summary>
internal sealed class ApprovalQueries(WorkflowDbContext db, IExecutionContext execution) : IApprovalQueries
{
    public async Task<IReadOnlyList<ApprovalView>> List(string? status, string? businessRefType, Guid? businessRefId, Guid? afterId, int limit,
        CancellationToken cancellationToken)
    {
        var query = db.Approvals.AsNoTracking();
        if (!execution.CurrentScope.IsUnrestricted)
        {
            var inScope = db.Approvals.AsNoTracking().InScope(execution.CurrentScope, a => a.ProductCode, a => a.OrgNodeCode).Select(a => a.Id);
            query = query.Where(a => !a.IsScoped || inScope.Contains(a.Id));
        }

        if (status is not null && Enum.TryParse<ApprovalStatus>(status, ignoreCase: true, out var parsed))
        {
            query = query.Where(a => a.Status == parsed);
        }

        if (businessRefType is not null)
        {
            query = query.Where(a => a.BusinessRefType == businessRefType);
        }

        if (businessRefId is not null)
        {
            query = query.Where(a => a.BusinessRefId == businessRefId);
        }

        if (afterId is not null)
        {
            query = query.Where(a => a.Id.CompareTo(afterId.Value) > 0);
        }

        return (await query.OrderBy(a => a.Id).Take(limit).ToListAsync(cancellationToken)).Select(View).ToList();
    }

    public async Task<ApprovalView?> Get(Guid id, CancellationToken cancellationToken) =>
        await db.Approvals.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, cancellationToken) is { } approval
        && (!approval.IsScoped || execution.CurrentScope.Allows(approval.ProductCode, approval.OrgNodeCode))
            ? View(approval)
            : null;

    public async Task<IReadOnlyList<Approval>> WaitingFromOthers(Guid userId, string? businessRefType, Guid? businessRefId, Guid? afterId, int limit,
        CancellationToken cancellationToken)
    {
        var query = db.Approvals.AsNoTracking().Where(a => a.Status == ApprovalStatus.Requested && a.MakerId != userId);
        if (!execution.CurrentScope.IsUnrestricted)
        {
            var inScope = db.Approvals.AsNoTracking().InScope(execution.CurrentScope, a => a.ProductCode, a => a.OrgNodeCode).Select(a => a.Id);
            query = query.Where(a => !a.IsScoped || inScope.Contains(a.Id));
        }

        query = businessRefType is null ? query : query.Where(a => a.BusinessRefType == businessRefType);
        query = businessRefId is null ? query : query.Where(a => a.BusinessRefId == businessRefId);
        query = afterId is null ? query : query.Where(a => a.Id.CompareTo(afterId.Value) > 0);
        return await query.OrderBy(a => a.Id).Take(limit).ToListAsync(cancellationToken);
    }

    private static ApprovalView View(Approval a) => ApprovalView.Of(a);
}

internal sealed class ProcessRepository(WorkflowDbContext db) : IProcessRepository
{
    public Task<ProcessInstance?> Get(Guid id, CancellationToken cancellationToken) =>
        db.Processes.Include(p => p.Tasks).Include(p => p.Timers).SingleOrDefaultAsync(p => p.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Guid>> WithDueTimers(DateTimeOffset now, int limit, CancellationToken cancellationToken) =>
        await db.Set<ProcessTimer>().Where(t => t.FiredAt == null && t.FireAt <= now).Select(t => t.ProcessInstanceId).Distinct().Take(limit)
            .ToListAsync(cancellationToken);

    public void Add(ProcessInstance process) => db.Processes.Add(process);
}

internal sealed class ProcessQueries(WorkflowDbContext db) : IProcessQueries
{
    public async Task<IReadOnlyList<ProcessView>> List(string? businessRefType, Guid? businessRefId, Guid? afterId, int limit, CancellationToken cancellationToken)
    {
        var query = db.Processes.AsNoTracking().Include(p => p.Tasks).AsQueryable();
        if (businessRefType is not null)
        {
            query = query.Where(p => p.BusinessRefType == businessRefType);
        }

        if (businessRefId is not null)
        {
            query = query.Where(p => p.BusinessRefId == businessRefId);
        }

        if (afterId is not null)
        {
            query = query.Where(p => p.Id.CompareTo(afterId.Value) > 0);
        }

        var processes = await query.OrderBy(p => p.Id).Take(limit).ToListAsync(cancellationToken);
        return processes.Select(p => View(p, [])).ToList();
    }

    public async Task<ProcessView?> Get(Guid id, CancellationToken cancellationToken)
    {
        var process = await db.Processes.AsNoTracking().Include(p => p.Tasks).SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (process is null)
        {
            return null;
        }

        var notes = await db.CaseNotes.AsNoTracking().Where(n => n.BusinessRefType == process.BusinessRefType && n.BusinessRefId == process.BusinessRefId)
            .OrderBy(n => n.Id).ToListAsync(cancellationToken);
        return View(process, notes.Select(n => new CaseNoteView(n.AuthorId, n.Note, db.Entry(n).Property<DateTimeOffset>("CreatedAt").CurrentValue)).ToList());
    }

    public async Task<IReadOnlyList<OpenTaskView>> OpenTasks(string? queueCode, Guid? assigneeId, OpenTaskCursor? after, int limit, CancellationToken cancellationToken)
    {
        var query = db.Set<ProcessTask>().AsNoTracking().Where(t => t.Status == Domain.Processes.TaskStatus.Open);
        if (queueCode is not null)
        {
            query = query.Where(t => t.QueueCode == queueCode);
        }

        if (assigneeId is not null)
        {
            query = query.Where(t => t.AssigneeId == assigneeId);
        }

        if (after is { } cursor)
        {
            query = query.Where(t => t.DueAt > cursor.DueAt || (t.DueAt == cursor.DueAt && t.Id.CompareTo(cursor.Id) > 0));
        }

        return (await query.OrderBy(t => t.DueAt).ThenBy(t => t.Id).Take(limit).ToListAsync(cancellationToken))
            .Select(t => new OpenTaskView(t.ProcessInstanceId, View(t))).ToList();
    }

    private static ProcessView View(ProcessInstance p, IReadOnlyList<CaseNoteView> notes) =>
        new(p.Id, p.ProcessCode, p.ProcessVersion, p.BusinessRefType, p.BusinessRefId, p.Status, p.DefinitionState, p.StartedAt, p.EndedAt,
            p.Tasks.OrderBy(t => t.Id).Select(View).ToList(), notes, p.RowVersion);

    private static TaskView View(ProcessTask t) =>
        new(t.Id, t.TaskType, t.QueueCode, t.AssigneeId, t.Status, t.SlaCode, t.DueAt, t.CompletedAt, t.Outcome);
}

public sealed class WorkflowOptions
{
    /// <summary>TODO(spec): the due time of a task without an SLA definition is not specified.</summary>
    public TimeSpan DefaultTaskDue { get; set; } = TimeSpan.FromDays(5);

    public string DefaultQueue { get; set; } = "DEFAULT";

    public TimeSpan TimerInterval { get; set; } = TimeSpan.FromMinutes(1);

    public bool TimersEnabled { get; set; } = true;
}

/// <summary>Reads workflow configuration from the Workflow module's static data in force.</summary>
internal sealed class WorkflowConfiguration(WorkflowDbContext db, IOptions<WorkflowOptions> options, IHolidayCalendar holidayCalendar, ITenantContext tenant,
    ILogger<WorkflowConfiguration> logger) : IWorkflowConfiguration, IAuthorityReader, IApprovalExpiry
{
    public async Task<Result<ProcessDefinition>> Definition(string processCode, int? version, DateOnly day, CancellationToken cancellationToken)
    {
        if (BuiltInProcesses.Find(processCode, version) is { } builtIn)
        {
            return builtIn;
        }

        var definitions = db.Set<WorkflowDefinitions>().AsNoTracking();
        var row = version is { } v
            ? await definitions.Where(d => d.ProcessCode == processCode && d.Version == v && d.TenantId != null).SingleOrDefaultAsync(cancellationToken)
            : await definitions.InForce(Descriptor<WorkflowDefinitions>().Lifecycle, day).Where(d => d.ProcessCode == processCode)
                .OrderByDescending(d => d.TenantId != null).FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return Error.NotFound(ErrorCodes.NotFound, $"No workflow definition {processCode}{(version is null ? " is in force" : $" version {version}")}.");
        }

        var transitions = new List<TransitionDefinition>();
        if (row.Transitions.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in row.Transitions.EnumerateArray())
            {
                var parsed = Transition(item);
                if (parsed.IsFailure)
                {
                    return parsed.Error;
                }

                transitions.Add(parsed.Value);
            }
        }

        var definition = new ProcessDefinition(row.ProcessCode, row.Version, row.States, transitions, string.IsNullOrWhiteSpace(row.DomainValidation) ? null : row.DomainValidation);
        var valid = definition.Validate();
        return valid.IsFailure ? valid.Error : definition;
    }

    public async Task<Func<string, TaskRouting>> Routing(DateOnly day, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var queues = await db.Set<TaskTypesAndWorkQueues>().AsNoTracking().InForce(Descriptor<TaskTypesAndWorkQueues>().Lifecycle, day).ToListAsync(cancellationToken);
        var slas = await db.Set<SLADefinitions>().AsNoTracking().InForce(Descriptor<SLADefinitions>().Lifecycle, day).ToListAsync(cancellationToken);
        var calendars = await Calendars(slas.Select(s => s.Calendar).Distinct().ToArray(), day, cancellationToken);
        var settings = options.Value;
        return taskType =>
        {
            var queue = queues.FirstOrDefault(q => q.TaskTypes.Contains(taskType))?.QueueCode ?? settings.DefaultQueue;
            var sla = slas.FirstOrDefault(s => s.TaskType == taskType);
            var due = sla is null ? now + settings.DefaultTaskDue : Due(now, sla.Duration, calendars.GetValueOrDefault(sla.Calendar));
            return new TaskRouting(queue, sla?.SlaCode, due);
        };
    }

    /// <summary>
    /// The business calendars the SLAs name, with their public holidays for the next 400 days (OQ-049). An SLA whose
    /// calendar is not in force runs on the clock, and the gap is logged.
    /// </summary>
    internal async Task<Dictionary<string, BusinessCalendar>> Calendars(string[] codes, DateOnly day, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, BusinessCalendar>(StringComparer.Ordinal);
        var rows = await db.Set<BusinessCalendars>().AsNoTracking().InForce(Descriptor<BusinessCalendars>().Lifecycle, day)
            .Where(c => codes.Contains(c.CalendarCode)).ToListAsync(cancellationToken);
        foreach (var row in rows.GroupBy(r => r.CalendarCode).Select(g => g.OrderByDescending(r => r.TenantId != null).First()))
        {
            var holidays = string.IsNullOrWhiteSpace(row.HolidayCalendar)
                ? []
                : await holidayCalendar.Holidays(tenant.RequireTenantId(), row.HolidayCalendar, day, day.AddDays(400), cancellationToken);
            var calendar = BusinessCalendar.Create(row.CalendarCode, row.WorkingDays, row.Hours, row.TimeZone, holidays);
            if (calendar.IsSuccess)
            {
                result[row.CalendarCode] = calendar.Value;
            }
            else
            {
                logger.LogWarning("Business calendar {Calendar} cannot be used: {Message}", row.CalendarCode, calendar.Error.Message);
            }
        }

        foreach (var missing in codes.Where(c => !result.ContainsKey(c)))
        {
            logger.LogWarning("SLA calendar {Calendar} is not in force; its SLAs run on the clock", missing);
        }

        return result;
    }

    public async Task<(int Level, string TargetRole)?> NextEscalation(int currentLevel, DateOnly day, CancellationToken cancellationToken)
    {
        var rule = await db.Set<EscalationRules>().AsNoTracking().InForce(Descriptor<EscalationRules>().Lifecycle, day)
            .Where(r => r.Trigger == "SLA_BREACH" && r.Level > currentLevel).OrderBy(r => r.Level).FirstOrDefaultAsync(cancellationToken);
        return rule is null ? null : (rule.Level, rule.TargetRole);
    }

    public async Task<IReadOnlyList<AuthorityGrant>> For(IEnumerable<string> roleCodes, DateOnly day, CancellationToken cancellationToken)
    {
        var roles = roleCodes.ToArray();
        return await db.Set<DelegationAndApprovalAuthority>().AsNoTracking().InForce(Descriptor<DelegationAndApprovalAuthority>().Lifecycle, day)
            .Where(a => roles.Contains(a.RoleCode)).Select(a => new AuthorityGrant(a.RoleCode, a.TransactionType, a.LimitValue, a.Currency)).ToListAsync(cancellationToken);
    }

    /// <summary>D-4: how long an approval of the type waits, from workflow.cfg_approval_expiry in force; null for the default.</summary>
    public async Task<TimeSpan?> For(string approvalType, DateOnly day, CancellationToken cancellationToken)
    {
        var row = await db.Set<ApprovalExpiry>().AsNoTracking().InForce(Descriptor<ApprovalExpiry>().Lifecycle, day)
            .Where(e => e.ApprovalType == approvalType).Select(e => e.ExpiresAfter).FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : System.Xml.XmlConvert.ToTimeSpan(row);
    }

    public Task<bool> IsConfigured(string approvalType, DateOnly day, CancellationToken cancellationToken) =>
        db.Set<DelegationAndApprovalAuthority>().AsNoTracking().InForce(Descriptor<DelegationAndApprovalAuthority>().Lifecycle, day)
            .AnyAsync(a => a.TransactionType == approvalType, cancellationToken);

    /// <summary>A transition is {"from", "to", "when"?} or the text form "From → To when condition" (catalogue sample).</summary>
    private static Result<TransitionDefinition> Transition(JsonElement item)
    {
        string? from, to, when = null;
        if (item.ValueKind == JsonValueKind.Object)
        {
            from = item.TryGetProperty("from", out var f) ? f.GetString() : null;
            to = item.TryGetProperty("to", out var t) ? t.GetString() : null;
            when = item.TryGetProperty("when", out var w) ? w.GetString() : null;
        }
        else if (item.ValueKind == JsonValueKind.String && item.GetString() is { } text && text.Split(['→'], 2) is [var left, var right])
        {
            from = left.Trim();
            var parts = right.Split(" when ", 2, StringSplitOptions.TrimEntries);
            to = parts[0];
            when = parts.Length == 2 ? parts[1] : null;
        }
        else
        {
            from = to = null;
        }

        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
        {
            return Error.Validation(WorkflowErrorCodes.DefinitionInvalid, $"Transition {item} needs a from and a to state.");
        }

        if (when is null)
        {
            return new TransitionDefinition(from, to, null);
        }

        var guard = Expression.Parse(when);
        return guard.IsFailure ? guard.Error : new TransitionDefinition(from, to, guard.Value);
    }

    /// <summary>The SLA due time: on its business calendar when there is one (days are working days), otherwise on the clock.</summary>
    internal static DateTimeOffset Due(DateTimeOffset now, string isoDuration, BusinessCalendar? calendar)
    {
        var period = PeriodPattern.NormalizingIso.Parse(isoDuration).GetValueOrThrow();
        if (calendar is null)
        {
            var local = LocalDateTime.FromDateTime(now.UtcDateTime).Plus(period);
            return new DateTimeOffset(local.ToDateTimeUnspecified(), TimeSpan.Zero);
        }

        var days = (int)((period.Years * 365) + (period.Months * 30) + (period.Weeks * 7) + period.Days);
        var time = TimeSpan.FromHours(period.Hours) + TimeSpan.FromMinutes(period.Minutes) + TimeSpan.FromSeconds(period.Seconds);
        return calendar.Add(now, days, time);
    }

    private static StaticDataDescriptor Descriptor<T>() => WorkflowStaticData.Descriptors.Single(d => d.EntityType == typeof(T));
}

/// <summary>
/// Business time in the current tenant (ADR-022 decision 6): the named calendar when in force, else Monday to Friday,
/// 08:00 to 17:00, in the tenant's time zone.
/// </summary>
internal sealed class BusinessTime(WorkflowConfiguration configuration, ITenantTimeZone timeZones, ITenantContext tenant, BuildingBlocks.Application.Context.IBusinessDate businessDate) : IBusinessTime
{
    public const string DefaultTimeZone = "Africa/Johannesburg";

    public async Task<DateTimeOffset> Add(Guid tenantId, string? calendarCode, DateTimeOffset start, string isoDuration, CancellationToken cancellationToken)
    {
        if (tenant.RequireTenantId() != tenantId)
        {
            throw new InvalidOperationException("Business time is read in the tenant's own scope (TEN-004).");
        }

        BusinessCalendar? calendar = null;
        if (!string.IsNullOrWhiteSpace(calendarCode))
        {
            calendar = (await configuration.Calendars([calendarCode], businessDate.Today, cancellationToken)).GetValueOrDefault(calendarCode);
        }

        if (calendar is null)
        {
            var zone = await timeZones.TimeZone(tenantId, businessDate.Today, cancellationToken);
            var inZone = Default(string.IsNullOrWhiteSpace(zone) ? DefaultTimeZone : zone);
            calendar = inZone.IsSuccess ? inZone.Value : Default(DefaultTimeZone).Value;
        }

        return WorkflowConfiguration.Due(start, isoDuration, calendar);
    }

    private static Result<BusinessCalendar> Default(string timeZone) =>
        BusinessCalendar.Create("DEFAULT", ["Mon", "Tue", "Wed", "Thu", "Fri"], "08:00-17:00", timeZone, []);
}

internal sealed class WorkflowEventMapper : IDomainEventMapper
{
    public IIntegrationEvent? Map(IDomainEvent domainEvent) => domainEvent switch
    {
        Domain.Approvals.ApprovalGranted e => new Contracts.ApprovalGranted(e.ApprovalId, e.BusinessRefType, e.BusinessRefId, e.ApprovalType, e.MakerId, e.CheckerId, e.SubjectHash),
        Domain.Approvals.ApprovalRejected e => new Contracts.ApprovalRejected(e.ApprovalId, e.BusinessRefType, e.BusinessRefId, e.ApprovalType, e.MakerId, e.CheckerId, e.Reason, e.SubjectHash,
            e.Ending),
        Domain.Processes.SlaBreached e => new Contracts.SlaBreached(e.ProcessInstanceId, e.TaskId, e.TaskType, e.SlaCode),
        _ => null,
    };
}

/// <summary>D-4: an approval expiry is an ISO 8601 duration of at least an hour, in calendar time.</summary>
internal sealed class ApprovalExpiryValidator : BuildingBlocks.Infrastructure.StaticData.IStaticRecordValidator
{
    public string Schema => WorkflowDbContext.SchemaName;

    public string Table => "cfg_approval_expiry";

    public string Rules => "expiresAfter is an ISO 8601 duration of at least one hour (P14D, PT48H), counted in calendar time from the request; "
        + "a type with none in force waits 14 days.";

    public IReadOnlyDictionary<string, string[]> Validate(BuildingBlocks.Infrastructure.StaticData.StaticRecord record)
    {
        if (record is not ApprovalExpiry expiry)
        {
            return new Dictionary<string, string[]>();
        }

        try
        {
            return System.Xml.XmlConvert.ToTimeSpan(expiry.ExpiresAfter) >= TimeSpan.FromHours(1)
                ? new Dictionary<string, string[]>()
                : new Dictionary<string, string[]> { ["expiresAfter"] = ["At least one hour."] };
        }
        catch (FormatException)
        {
            return new Dictionary<string, string[]> { ["expiresAfter"] = ["An ISO 8601 duration, such as P14D."] };
        }
    }
}

/// <summary>OQ-113: a record's latest approval ending other than approval, on Workflow's own connection, in the caller's tenant.</summary>
internal sealed class ApprovalHistory([Microsoft.Extensions.DependencyInjection.FromKeyedServices(WorkflowDbContext.SchemaName)] Npgsql.NpgsqlDataSource dataSource,
    ITenantContext tenant) : IApprovalHistory
{
    public async Task<LatestRejection?> Latest(string businessRefType, Guid businessRefId, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await BuildingBlocks.Infrastructure.Persistence.TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
        await using var command = new Npgsql.NpgsqlCommand("""
            SELECT status, approval_type, checker_id, coalesce(decided_at, requested_at), coalesce(decision_reason, '') FROM workflow.approval
            WHERE tenant_id = @t AND business_ref_type = @type AND business_ref_id = @id AND status IN ('REJECTED', 'EXPIRED', 'WITHDRAWN')
            ORDER BY coalesce(decided_at, requested_at) DESC LIMIT 1
            """, connection, transaction);
        command.Parameters.AddWithValue("t", tenantId);
        command.Parameters.AddWithValue("type", businessRefType);
        command.Parameters.AddWithValue("id", businessRefId);
        LatestRejection? latest = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                latest = new LatestRejection(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetGuid(2),
                    reader.GetFieldValue<DateTimeOffset>(3), reader.GetString(4));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return latest;
    }

    public async Task<Guid?> Waiting(string businessRefType, Guid businessRefId, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await BuildingBlocks.Infrastructure.Persistence.TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
        await using var command = new Npgsql.NpgsqlCommand("""
            SELECT id FROM workflow.approval
            WHERE tenant_id = @t AND business_ref_type = @type AND business_ref_id = @id AND status = 'REQUESTED'
            ORDER BY requested_at DESC LIMIT 1
            """, connection, transaction);
        command.Parameters.AddWithValue("t", tenantId);
        command.Parameters.AddWithValue("type", businessRefType);
        command.Parameters.AddWithValue("id", businessRefId);
        var id = await command.ExecuteScalarAsync(cancellationToken) as Guid?;
        await transaction.CommitAsync(cancellationToken);
        return id;
    }

    public async Task<ApprovalRequest?> LatestRequest(string businessRefType, Guid businessRefId, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await BuildingBlocks.Infrastructure.Persistence.TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
        await using var command = new Npgsql.NpgsqlCommand("""
            SELECT maker_id, requested_at FROM workflow.approval
            WHERE tenant_id = @t AND business_ref_type = @type AND business_ref_id = @id
            ORDER BY requested_at DESC LIMIT 1
            """, connection, transaction);
        command.Parameters.AddWithValue("t", tenantId);
        command.Parameters.AddWithValue("type", businessRefType);
        command.Parameters.AddWithValue("id", businessRefId);
        ApprovalRequest? latest = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                latest = new ApprovalRequest(reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return latest;
    }
}
