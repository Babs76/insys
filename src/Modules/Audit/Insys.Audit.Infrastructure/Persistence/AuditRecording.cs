/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Audit
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
using Insys.Audit.Application;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Contracts.Auditing;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace Insys.Audit.Infrastructure.Persistence;

/// <summary>
/// The Audit context's policy "All sensitive operations → Record audit event" (domain-model.json). Every module publishes
/// its audit entries through its own outbox; this records them, once each (the event ID is the audit row ID).
/// </summary>
internal sealed class SensitiveOperationRecordedHandler(AuditDbContext db) : IIntegrationEventHandler<SensitiveOperationRecorded>
{
    public Task Handle(SensitiveOperationRecorded recorded, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        db.AuditEvents.Add(new AuditEvent
        {
            TenantId = envelope.TenantId,
            Id = envelope.EventId,
            EventType = Truncate(recorded.EventType, 40),
            ActorType = Truncate(recorded.ActorType, 10),
            ActorId = Truncate(recorded.ActorId, 80),
            Operation = Truncate(recorded.Operation, 40),
            TargetType = Truncate(recorded.TargetType, 30),
            TargetId = Truncate(recorded.TargetId, 80),
            Outcome = Truncate(recorded.Outcome, 10),
            OccurredAt = recorded.OccurredAt,
            CorrelationId = envelope.CorrelationId,
            Details = recorded.Details is null ? null : JsonSerializer.SerializeToElement(recorded.Details),
        });
        return Task.CompletedTask;
    }

    internal static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}

/// <summary>Writes audit records that have no business transaction, such as denied requests (TEN-005), on the Audit role.</summary>
internal sealed class AuditSink([FromKeyedServices(AuditDbContext.SchemaName)] NpgsqlDataSource dataSource) : IAuditSink
{
    public async Task Record(AuditRecord record, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, record.TenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO audit.audit_event
              (tenant_id, id, created_at, created_by, event_type, actor_type, actor_id, operation, target_type, target_id, outcome, occurred_at, correlation_id, details)
            VALUES (@tenant_id, @id, @at, 'audit-sink', @event_type, @actor_type, @actor_id, @operation, @target_type, @target_id, @outcome, @at, @correlation_id, @details)
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant_id", record.TenantId);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("at", record.OccurredAt);
        command.Parameters.AddWithValue("event_type", SensitiveOperationRecordedHandler.Truncate(record.Entry.EventType, 40));
        command.Parameters.AddWithValue("actor_type", record.ActorType);
        command.Parameters.AddWithValue("actor_id", SensitiveOperationRecordedHandler.Truncate(record.ActorId, 80));
        command.Parameters.AddWithValue("operation", SensitiveOperationRecordedHandler.Truncate(record.Entry.Operation, 40));
        command.Parameters.AddWithValue("target_type", SensitiveOperationRecordedHandler.Truncate(record.Entry.TargetType, 30));
        command.Parameters.AddWithValue("target_id", SensitiveOperationRecordedHandler.Truncate(record.Entry.TargetId, 80));
        command.Parameters.AddWithValue("outcome", record.Outcome.ToString().ToUpperInvariant());
        command.Parameters.AddWithValue("correlation_id", record.CorrelationId);
        command.Parameters.Add(new NpgsqlParameter("details", NpgsqlDbType.Jsonb)
        {
            Value = record.Entry.Details is null ? DBNull.Value : JsonSerializer.Serialize(record.Entry.Details),
        });
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}

internal sealed class AuditEventQueries(AuditDbContext db) : IAuditEventQueries
{
    public async Task<IReadOnlyList<AuditEventView>> List(string? targetType, string? targetId, string? actorId, Guid? afterId, int limit,
        CancellationToken cancellationToken)
    {
        var query = db.AuditEvents.AsNoTracking();
        if (targetType is not null)
        {
            query = query.Where(e => e.TargetType == targetType);
        }

        if (targetId is not null)
        {
            query = query.Where(e => e.TargetId == targetId);
        }

        if (actorId is not null)
        {
            query = query.Where(e => e.ActorId == actorId);
        }

        if (afterId is not null)
        {
            query = query.Where(e => e.Id.CompareTo(afterId.Value) < 0);
        }

        return await query.OrderByDescending(e => e.Id).Take(limit)
            .Select(e => new AuditEventView(e.Id, e.EventType, e.ActorType, e.ActorId, e.Operation, e.TargetType, e.TargetId, e.Outcome, e.OccurredAt,
                e.CorrelationId, e.Details))
            .ToListAsync(cancellationToken);
    }
}
