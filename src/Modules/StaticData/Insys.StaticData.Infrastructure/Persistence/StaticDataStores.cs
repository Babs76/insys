using System.Text.Json;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Contracts.StaticData;
using Insys.BuildingBlocks.Domain;
using Insys.StaticData.Application.Changes;
using Insys.StaticData.Domain.Changes;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.StaticData.Infrastructure.Persistence;

internal sealed class ChangeRequestRepository(StaticDataDbContext db) : IChangeRequestRepository
{
    public Task<StaticDataChange?> Get(Guid id, CancellationToken cancellationToken) => db.Changes.SingleOrDefaultAsync(c => c.Id == id, cancellationToken);

    public Task<StaticDataChange?> FindPending(string entityCode, string recordKey, CancellationToken cancellationToken) =>
        db.Changes.SingleOrDefaultAsync(c => c.EntityCode == entityCode && c.RecordKey == recordKey && c.Status == ChangeStatus.PendingApproval, cancellationToken);

    public void Add(StaticDataChange change) => db.Changes.Add(change);

    public void Add(ImportBatch batch) => db.ImportBatches.Add(batch);

    public Task<bool> BatchExists(Guid id, CancellationToken cancellationToken) => db.ImportBatches.AnyAsync(b => b.Id == id, cancellationToken);

    public Task<TenantGovernanceSettings?> Settings(CancellationToken cancellationToken) => db.Settings.SingleOrDefaultAsync(cancellationToken);

    public void Add(TenantGovernanceSettings settings) => db.Settings.Add(settings);
}

internal sealed class ChangeRequestQueries(StaticDataDbContext db) : IChangeRequestQueries
{
    public async Task<IReadOnlyList<ChangeRequestView>> List(string? status, string? entityCode, Guid? afterId, int limit, CancellationToken cancellationToken)
    {
        var query = db.Changes.AsNoTracking();
        if (status is not null && Enum.TryParse<ChangeStatus>(status.Replace("_", "", StringComparison.Ordinal), ignoreCase: true, out var parsed))
        {
            query = query.Where(c => c.Status == parsed);
        }

        if (entityCode is not null)
        {
            query = query.Where(c => c.EntityCode == entityCode);
        }

        if (afterId is not null)
        {
            query = query.Where(c => c.Id.CompareTo(afterId.Value) > 0);
        }

        return (await query.OrderBy(c => c.Id).Take(limit).ToListAsync(cancellationToken)).Select(View).ToList();
    }

    public async Task<ChangeRequestView?> Get(Guid id, CancellationToken cancellationToken) =>
        await db.Changes.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, cancellationToken) is { } change ? View(change) : null;

    public async Task<IReadOnlyList<ImportBatchView>> Imports(Guid? afterId, int limit, CancellationToken cancellationToken)
    {
        var query = db.ImportBatches.AsNoTracking().Include(b => b.Errors).AsQueryable();
        if (afterId is not null)
        {
            query = query.Where(b => b.Id.CompareTo(afterId.Value) > 0);
        }

        return (await query.OrderBy(b => b.Id).Take(limit).ToListAsync(cancellationToken))
            .Select(b => new ImportBatchView(b.Id, b.BatchNumber, b.EntityCode, b.FileRef, b.RowCount, b.RejectedCount, b.Status.ToString(), b.SubmittedBy,
                b.Errors.OrderBy(e => e.LineNo).Select(e => new ImportLineError(e.LineNo, e.Field, e.Message)).ToList()))
            .ToList();
    }

    private static ChangeRequestView View(StaticDataChange c)
    {
        using var record = JsonDocument.Parse(c.After);
        return new ChangeRequestView(c.Id, c.RequestNumber, c.EntityCode, c.RecordKey, c.Action, c.Status.ToString(), c.MakerId, c.CheckerId, c.EffectiveFrom,
            c.Reason, record.RootElement.Clone(), c.RowVersion);
    }
}

internal sealed class StaticDataEventMapper : IDomainEventMapper
{
    public IIntegrationEvent? Map(IDomainEvent domainEvent) => domainEvent switch
    {
        Domain.Changes.StaticDataChangeApproved approved =>
            new BuildingBlocks.Contracts.StaticData.StaticDataChangeApproved(approved.ChangeRequestId, Record(approved.RecordRef), approved.CheckerId, approved.OccurredAt),
        Domain.Changes.StaticDataApprovalNeeded needed =>
            new BuildingBlocks.Contracts.Approvals.ApprovalRequested(Contracts.StaticDataApprovals.BusinessRefType, needed.ChangeRequestId, needed.ApprovalType,
                needed.MakerId, null, null, needed.Reason),
        Domain.Changes.GovernanceChangeRequested requested =>
            new BuildingBlocks.Contracts.Approvals.ApprovalRequested(Contracts.StaticDataApprovals.GovernanceBusinessRefType, requested.SettingsId, requested.ApprovalType,
                requested.RequestedBy, null, null, "Switch Tier 2 maker-checker off"),
        Domain.Changes.StaticDataChangeRejected rejected =>
            new BuildingBlocks.Contracts.StaticData.StaticDataChangeRejected(rejected.ChangeRequestId, Record(rejected.RecordRef), rejected.CheckerId, rejected.Reason),
        _ => null,
    };

    private static StaticRecordRef Record(string json) =>
        JsonSerializer.Deserialize<StaticRecordRef>(json, StaticDataGovernancePolicies.Json) ?? throw new InvalidOperationException("Change request without a record reference.");
}

internal sealed class StaticDataChangeLookup([FromKeyedServices(StaticDataDbContext.SchemaName)] NpgsqlDataSource dataSource) : Contracts.IStaticDataChangeLookup
{
    public async Task<Guid?> PendingChange(Guid tenantId, string entityCode, string code, int version, bool global, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT id FROM sdm.change_request WHERE tenant_id = @t AND entity_code = @entity AND record_key = @key AND status = @pending
            """, connection, transaction);
        command.Parameters.AddWithValue("t", tenantId);
        command.Parameters.AddWithValue("entity", entityCode);
        command.Parameters.AddWithValue("key", StaticDataChange.KeyFor(code, version, global));
        command.Parameters.AddWithValue("pending", UpperCaseEnumConverter<ChangeStatus>.ToCode(ChangeStatus.PendingApproval));
        var id = await command.ExecuteScalarAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return id as Guid?;
    }
}

internal sealed class HolidayCalendar([FromKeyedServices(StaticDataDbContext.SchemaName)] NpgsqlDataSource dataSource) : Contracts.IHolidayCalendar
{
    private static readonly string[] InForce = StaticLifecycle.Reference.InForce;

    public async Task<IReadOnlyList<DateOnly>> Holidays(Guid tenantId, string calendarCode, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT date, observed_rule FROM refdata.ref_public_holiday_calendars
            WHERE calendar_code = @code AND record_status = ANY(@statuses) AND (tenant_id = @t OR tenant_id IS NULL)
              AND date BETWEEN @from AND @to
            """, connection, transaction);
        command.Parameters.AddWithValue("code", calendarCode);
        command.Parameters.AddWithValue("statuses", InForce);
        command.Parameters.AddWithValue("t", tenantId);
        command.Parameters.AddWithValue("from", from.AddDays(-1));
        command.Parameters.AddWithValue("to", to);
        var days = new List<DateOnly>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var date = reader.GetFieldValue<DateOnly>(0);
                var rule = reader.IsDBNull(1) ? null : reader.GetString(1);
                days.Add(date.DayOfWeek == DayOfWeek.Sunday && rule is not null && rule.Contains("MONDAY", StringComparison.OrdinalIgnoreCase) ? date.AddDays(1) : date);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return days.Where(d => d >= from && d <= to).Distinct().Order().ToList();
    }
}
