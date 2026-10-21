using System.Text.Json;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Contracts.StaticData;
using Insys.BuildingBlocks.Domain;
using Insys.StaticData.Application.Changes;
using Insys.StaticData.Domain.Changes;
using Microsoft.EntityFrameworkCore;

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
        Domain.Changes.StaticDataChangeRejected rejected =>
            new BuildingBlocks.Contracts.StaticData.StaticDataChangeRejected(rejected.ChangeRequestId, Record(rejected.RecordRef), rejected.CheckerId, rejected.Reason),
        _ => null,
    };

    private static StaticRecordRef Record(string json) =>
        JsonSerializer.Deserialize<StaticRecordRef>(json, StaticDataGovernancePolicies.Json) ?? throw new InvalidOperationException("Change request without a record reference.");
}
