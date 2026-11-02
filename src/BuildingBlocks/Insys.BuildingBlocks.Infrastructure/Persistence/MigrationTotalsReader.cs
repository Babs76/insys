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
using Insys.BuildingBlocks.Application.Context;
using Npgsql;

namespace Insys.BuildingBlocks.Infrastructure.Persistence;

/// <summary>Runs a module's totals query for a migration batch, in the tenant, under row-level security. It gets <c>@batch</c> and returns kind, count and amount.</summary>
public static class MigrationTotalsReader
{
    public static async Task<IReadOnlyList<MigrationTotal>> Read(NpgsqlDataSource dataSource, Guid tenantId, string batch, string sql, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("batch", batch);
        var totals = new List<MigrationTotal>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                totals.Add(new MigrationTotal(reader.GetString(0), reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetDecimal(2), reader.IsDBNull(3) ? null : reader.GetString(3).Trim()));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return totals;
    }
}

/// <summary>Runs a module's policy export query in the tenant. It returns number, product, holder, status, start, end and currency.</summary>
public static class PolicyExportReader
{
    public static async Task<IReadOnlyList<PolicyExportRow>> Read(NpgsqlDataSource dataSource, Guid tenantId, string domain, string sql, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        var rows = new List<PolicyExportRow>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new PolicyExportRow(domain, reader.GetString(0), reader.IsDBNull(1) ? "" : reader.GetString(1), reader.GetGuid(2), reader.GetString(3), reader.GetFieldValue<DateOnly>(4), reader.IsDBNull(5) ? null : reader.GetFieldValue<DateOnly>(5),
                    reader.IsDBNull(6) ? "" : reader.GetString(6)));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return rows;
    }
}
