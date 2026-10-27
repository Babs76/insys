/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Documents
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
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.Documents.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.Documents.Infrastructure.Persistence;

/// <summary>Legal holds for the party anonymisation check (D-34), in the current tenant.</summary>
internal sealed class RecordHolds([FromKeyedServices(DocumentsDbContext.SchemaName)] NpgsqlDataSource dataSource, ITenantContext tenant) : IRecordHolds
{
    public async Task<IReadOnlyList<RecordHold>> Holds(IReadOnlyCollection<Guid> recordIds, CancellationToken cancellationToken)
    {
        var holds = new List<RecordHold>();
        if (recordIds.Count == 0)
        {
            return holds;
        }

        await Read("""
            SELECT h.id, d.id, coalesce(d.business_ref_type, h.scope_type), coalesce(d.business_ref_id, h.scope_id), h.reason_code
            FROM document.legal_hold h
            LEFT JOIN document.document d ON h.scope_type = 'DOCUMENT' AND d.tenant_id = h.tenant_id AND d.id = h.scope_id
            WHERE h.tenant_id = @t AND h.released_at IS NULL AND (d.business_ref_id = ANY(@ids) OR (d.id IS NULL AND h.scope_id = ANY(@ids)))
            UNION ALL
            SELECT NULL, d.id, d.business_ref_type, d.business_ref_id, NULL
            FROM document.document d
            WHERE d.tenant_id = @t AND d.legal_hold AND d.business_ref_id = ANY(@ids)
              AND NOT EXISTS (SELECT 1 FROM document.legal_hold h
                              WHERE h.tenant_id = d.tenant_id AND h.scope_type = 'DOCUMENT' AND h.scope_id = d.id AND h.released_at IS NULL)
            ORDER BY 4, 2
            """, command => command.Parameters.AddWithValue("ids", recordIds.ToArray()),
            reader => holds.Add(new RecordHold(reader.IsDBNull(0) ? null : reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1),
                reader.GetString(2), reader.GetGuid(3), reader.IsDBNull(4) ? null : reader.GetString(4))), cancellationToken);
        return holds;
    }

    private async Task Read(string sql, Action<NpgsqlCommand> parameters, Action<NpgsqlDataReader> row, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Legal holds are read in a tenant.");
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("t", tenantId);
        parameters(command);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                row(reader);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
