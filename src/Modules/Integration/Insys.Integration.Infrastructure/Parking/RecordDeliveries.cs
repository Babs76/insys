/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Integration
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
using Insys.BuildingBlocks.Application.Context;
using Insys.Integration.Contracts;
using Insys.Integration.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.Integration.Infrastructure.Parking;

/// <summary>
/// A record's open delivery to one consumer, read from integration.parked_delivery in the record's tenant on the
/// Integration module's own role, and an operator's retry of it (BR-018).
/// </summary>
internal sealed class RecordDeliveries([FromKeyedServices(IntegrationDbContext.SchemaName)] NpgsqlDataSource dataSource, IClock clock) : IRecordDeliveries
{
    public Task<RecordDelivery?> Open(Guid tenantId, string consumer, string aggregateType, Guid aggregateId, CancellationToken cancellationToken) =>
        ParkedRows.InTenant(dataSource, tenantId, async (connection, transaction) =>
        {
            await using var command = ParkedRows.Command($"""
                SELECT status, prerequisite_code, prerequisite_item FROM integration.parked_delivery
                WHERE tenant_id = @tenant_id AND consumer = @consumer AND aggregate_type = @aggregate_type AND aggregate_id = @aggregate_id
                  AND status IN {ParkedRows.OpenStatuses}
                ORDER BY occurred_at DESC, event_id DESC
                LIMIT 1
                """, connection, transaction,
                ("tenant_id", tenantId), ("consumer", consumer), ("aggregate_type", aggregateType), ("aggregate_id", aggregateId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken)
                ? new RecordDelivery(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2))
                : null;
        }, cancellationToken);

    public Task<int> Retry(Guid tenantId, string consumer, string aggregateType, Guid aggregateId, CancellationToken cancellationToken) =>
        ParkedRows.InTenant(dataSource, tenantId, async (connection, transaction) =>
        {
            // Parked or dead-lettered, the delivery becomes the ledger's to retry, due now: the parking job's next run takes it,
            // and it parks again if its cause is still there. Until then it reads as awaited, not parked or failed.
            await using var command = ParkedRows.Command("""
                UPDATE integration.parked_delivery
                SET status = 'FAILING', ledger_owned = true, next_attempt_at = @now, retry_requested_at = NULL,
                    row_version = row_version + 1, updated_at = @now, updated_by = 'operator-retry'
                WHERE tenant_id = @tenant_id AND consumer = @consumer AND aggregate_type = @aggregate_type AND aggregate_id = @aggregate_id
                  AND status IN ('PARKED', 'DEAD_LETTERED')
                """, connection, transaction,
                ("tenant_id", tenantId), ("consumer", consumer), ("aggregate_type", aggregateType), ("aggregate_id", aggregateId), ("now", clock.UtcNow));
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);
}
