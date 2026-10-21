using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Insys.BuildingBlocks.Infrastructure.Messaging;

/// <summary>
/// Wraps a consumer's event handler. In the consumer module's transaction it first records (consumer, event_id) in
/// integration.inbox_message; if the row already exists the event is a duplicate and is skipped (INT-005, DBA-007).
/// The handler's changes, its outbox events and the inbox row commit together.
/// </summary>
public sealed class InboxEventHandler<TEvent, THandler, TDbContext>(
    THandler inner,
    TDbContext db,
    IClock clock)
    where TEvent : IIntegrationEvent
    where THandler : IIntegrationEventHandler<TEvent>
    where TDbContext : ModuleDbContext
{
    private const string InsertSql = """
        INSERT INTO integration.inbox_message
          (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, consumer, event_id, event_type, received_at, processed_at, status)
        VALUES
          (@tenant_id, @id, 1, @now, @consumer, @now, @consumer, @consumer, @event_id, @event_type, @now, @now, 'PROCESSED')
        ON CONFLICT (tenant_id, consumer, event_id) DO NOTHING
        """;

    /// <summary>Returns false when the event was already processed by this consumer.</summary>
    public async Task<bool> Handle(string consumer, TEvent integrationEvent, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = clock.UtcNow;
        await using (var command = new NpgsqlCommand(InsertSql, (NpgsqlConnection)db.Database.GetDbConnection(), (NpgsqlTransaction)transaction.GetDbTransaction()))
        {
            command.Parameters.AddWithValue("tenant_id", envelope.TenantId);
            command.Parameters.AddWithValue("id", Guid.CreateVersion7());
            command.Parameters.AddWithValue("now", now);
            command.Parameters.AddWithValue("consumer", consumer);
            command.Parameters.AddWithValue("event_id", envelope.EventId);
            command.Parameters.AddWithValue("event_type", envelope.EventType);
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                await transaction.CommitAsync(cancellationToken);
                return false;
            }
        }

        await inner.Handle(integrationEvent, envelope, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
