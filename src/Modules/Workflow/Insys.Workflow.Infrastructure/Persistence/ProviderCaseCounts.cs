/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Workflow
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
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.Workflow.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.Workflow.Infrastructure.Persistence;

/// <summary>F029: cases concerning a provider, on Workflow's own connection for the tenant.</summary>
internal sealed class ProviderCaseCounts([FromKeyedServices(WorkflowDbContext.SchemaName)] NpgsqlDataSource dataSource) : IProviderCaseCounts
{
    public async Task<int> Count(Guid tenantId, string providerId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT count(*) FROM workflow.service_case c
            WHERE c.tenant_id = @t AND c.received_at >= @from AND c.received_at < @to
              AND (c.raised_by_provider_id = @p OR EXISTS (SELECT 1 FROM workflow.case_link l
                     WHERE l.tenant_id = c.tenant_id AND l.case_id = c.id AND l.link_type = 'PROVIDER' AND l.reference = @p))
            """, connection, transaction);
        command.Parameters.AddWithValue("t", tenantId);
        command.Parameters.AddWithValue("p", providerId);
        command.Parameters.AddWithValue("from", from);
        command.Parameters.AddWithValue("to", to);
        var count = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
        await transaction.CommitAsync(cancellationToken);
        return (int)count;
    }
}
