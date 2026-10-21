using System.Text.Json;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Npgsql;
using NpgsqlTypes;

namespace Insys.BuildingBlocks.Infrastructure.Auditing;

/// <summary>Collects the audit entries recorded during one command.</summary>
public sealed class AuditTrail : IAuditTrail
{
    private readonly List<AuditEntry> _entries = [];

    public void Record(AuditEntry entry) => _entries.Add(entry);

    public IReadOnlyList<AuditEntry> TakeAll()
    {
        var entries = _entries.ToArray();
        _entries.Clear();
        return entries;
    }
}

/// <summary>Inserts into audit.audit_event (insert-only, partitioned by month; IAM-006, DBA-016).</summary>
public sealed class AuditEventWriter(NpgsqlDataSource dataSource, IClock clock)
{
    private const string InsertSql = """
        INSERT INTO audit.audit_event
          (tenant_id, id, created_at, created_by, event_type, actor_type, actor_id, operation, target_type, target_id, outcome, occurred_at, correlation_id, details)
        VALUES
          (@tenant_id, @id, @now, @actor_id, @event_type, @actor_type, @actor_id, @operation, @target_type, @target_id, @outcome, @now, @correlation_id, @details)
        """;

    /// <summary>Writes in the caller's transaction, so the audit row commits with the business change.</summary>
    public async Task Write(NpgsqlConnection connection, NpgsqlTransaction transaction, IReadOnlyList<AuditEntry> entries, AuditOutcome outcome,
        Guid tenantId, IExecutionContext execution, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        foreach (var entry in entries)
        {
            await using var command = new NpgsqlCommand(InsertSql, connection, transaction);
            command.Parameters.AddWithValue("tenant_id", tenantId);
            command.Parameters.AddWithValue("id", Guid.CreateVersion7());
            command.Parameters.AddWithValue("now", now);
            command.Parameters.AddWithValue("actor_id", Truncate(execution.ActorId, 80));
            command.Parameters.AddWithValue("event_type", Truncate(entry.EventType, 40));
            command.Parameters.AddWithValue("actor_type", execution.ActorType == ActorType.Service ? "SERVICE" : "USER");
            command.Parameters.AddWithValue("operation", Truncate(entry.Operation, 40));
            command.Parameters.AddWithValue("target_type", Truncate(entry.TargetType, 30));
            command.Parameters.AddWithValue("target_id", Truncate(entry.TargetId, 80));
            command.Parameters.AddWithValue("outcome", outcome.ToString().ToUpperInvariant());
            command.Parameters.AddWithValue("correlation_id", execution.CorrelationId);
            command.Parameters.Add(new NpgsqlParameter("details", NpgsqlDbType.Jsonb)
            {
                Value = entry.Details is null ? DBNull.Value : JsonSerializer.Serialize(entry.Details),
            });
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>Writes in its own transaction, used when the business transaction rolled back.</summary>
    public async Task WriteIndependently(IReadOnlyList<AuditEntry> entries, AuditOutcome outcome, Guid tenantId, IExecutionContext execution,
        CancellationToken cancellationToken)
    {
        if (entries.Count == 0)
        {
            return;
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
        await Write(connection, transaction, entries, outcome, tenantId, execution, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}
