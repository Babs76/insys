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
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Integration.Contracts;
using Insys.Integration.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.Integration.Infrastructure.Partners;

/// <summary>The tenant's active partner of a type in force on a day (the partner register), read on Integration's own connection.</summary>
internal sealed class PartnerDirectory([FromKeyedServices(IntegrationDbContext.SchemaName)] NpgsqlDataSource dataSource) : IPartnerDirectory
{
    public async Task<string?> ActivePartner(Guid tenantId, string partnerType, DateOnly day, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT partner_code FROM integration.mst_partner_register
            WHERE tenant_id = @t AND partner_type = @type AND status = 'ACTIVE' AND record_status = ANY(@statuses)
              AND effective_from <= @day AND (effective_to IS NULL OR effective_to > @day)
            ORDER BY partner_code LIMIT 1
            """, connection, transaction);
        command.Parameters.AddWithValue("t", tenantId);
        command.Parameters.AddWithValue("type", partnerType);
        command.Parameters.AddWithValue("statuses", StaticLifecycle.Reference.InForce);
        command.Parameters.AddWithValue("day", day);
        var partner = await command.ExecuteScalarAsync(cancellationToken) as string;
        await transaction.CommitAsync(cancellationToken);
        return partner;
    }
}
