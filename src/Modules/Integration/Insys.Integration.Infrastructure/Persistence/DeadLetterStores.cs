using System.Text.Json;
using Insys.Integration.Application.DeadLetters;
using Insys.Integration.Domain.DeadLetters;
using Microsoft.EntityFrameworkCore;

namespace Insys.Integration.Infrastructure.Persistence;

internal sealed class DeadLetterRepository(IntegrationDbContext db) : IDeadLetterRepository
{
    public Task<DeadLetter?> Get(Guid id, CancellationToken cancellationToken) =>
        db.DeadLetters.SingleOrDefaultAsync(d => d.Id == id, cancellationToken);

    public void Add(DeadLetter deadLetter) => db.DeadLetters.Add(deadLetter);
}

internal sealed class DeadLetterQueries(IntegrationDbContext db) : IDeadLetterQueries
{
    public async Task<IReadOnlyList<DeadLetterSummary>> List(DeadLetterStatus? status, string? source, Guid? afterId, int limit, CancellationToken cancellationToken)
    {
        var query = db.DeadLetters.AsNoTracking();
        query = status switch
        {
            DeadLetterStatus.DeadLettered => query.Where(d => d.Resolution == null),
            DeadLetterStatus.Replayed => query.Where(d => d.Resolution == DeadLetterResolution.Replayed),
            DeadLetterStatus.Discarded => query.Where(d => d.Resolution == DeadLetterResolution.Discarded),
            _ => query,
        };

        if (!string.IsNullOrEmpty(source))
        {
            var sourceValue = new Source(source);
            query = query.Where(d => d.Source == sourceValue);
        }

        if (afterId is not null)
        {
            query = query.Where(d => d.Id.CompareTo(afterId.Value) > 0);
        }

        var rows = await query.OrderBy(d => d.Id).Take(limit).ToListAsync(cancellationToken);
        return rows.Select(d => new DeadLetterSummary(d.Id, d.Source.Value, d.MessageRef.Value, d.Status.ToString(), d.Attempts, d.FirstFailedAt)).ToList();
    }

    public async Task<DeadLetterDetail?> Get(Guid id, CancellationToken cancellationToken)
    {
        var d = await db.DeadLetters.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (d is null)
        {
            return null;
        }

        using var payload = JsonDocument.Parse(d.Payload);
        return new DeadLetterDetail(d.Id, d.Source.Value, d.MessageRef.Value, d.Status.ToString(), d.Attempts, d.FirstFailedAt, d.Error.Value,
            payload.RootElement.Clone(), d.ResolvedBy, d.RowVersion);
    }
}
