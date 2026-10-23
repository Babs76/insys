/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Infrastructure
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
using Insys.BuildingBlocks.Application.Auditing;
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
/// the module's own &lt;schema&gt;.inbox_message; if the row already exists the event is a duplicate and is skipped
/// (INT-005, DBA-007). The handler's changes, its outbox events and the inbox row commit together, in the module's
/// schema only (DBA-004).
/// </summary>
public sealed class InboxEventHandler<TEvent, THandler, TDbContext>(
    THandler inner,
    TDbContext db,
    IClock clock,
    Auditing.AuditTrail auditTrail)
    where TEvent : IIntegrationEvent
    where THandler : IIntegrationEventHandler<TEvent>
    where TDbContext : ModuleDbContext
{
    private const string InsertSql = """
        INSERT INTO {0}.inbox_message
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
        await using (var command = new NpgsqlCommand(string.Format(System.Globalization.CultureInfo.InvariantCulture, InsertSql, db.Schema), (NpgsqlConnection)db.Database.GetDbConnection(), (NpgsqlTransaction)transaction.GetDbTransaction()))
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

        // Sensitive operations a policy performs are audited like a command's (IAM-006), in the same transaction.
        db.PublishAudit(auditTrail.TakeAll(), AuditOutcome.Success);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
