/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Tenancy
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
using Insys.Tenancy.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.Tenancy.Infrastructure.Persistence;

/// <summary>Reads a tenant's function availability and feature entitlements (ADR-026 §2) on the Tenancy module's own connection.</summary>
internal sealed class TenantFunctionSettingsQuery([FromKeyedServices(TenancyDbContext.SchemaName)] NpgsqlDataSource dataSource) : ITenantFunctionSettings
{
    public async Task<TenantFunctionSettings> For(Guid tenantId, DateOnly day, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
        var disabled = new HashSet<string>(StringComparer.Ordinal);
        var features = new HashSet<string>(StringComparer.Ordinal);
        await using (var command = new NpgsqlCommand("""
            SELECT 'F', function_code FROM tenant.cfg_tenant_function_availability
            WHERE tenant_id = @t AND NOT enabled AND record_status = ANY(@statuses) AND effective_from <= @day AND (effective_to IS NULL OR effective_to > @day)
            UNION ALL
            SELECT 'E', e.feature FROM tenant.cfg_feature_entitlements e JOIN tenant.tenant t ON t.tenant_code = e.tenant AND t.id = @t
            WHERE e.tenant_id IS NULL AND e.enabled AND e.record_status = ANY(@statuses) AND e.effective_from <= @day AND (e.effective_to IS NULL OR e.effective_to > @day)
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("t", tenantId);
            command.Parameters.AddWithValue("statuses", StaticLifecycle.Configuration.InForce);
            command.Parameters.AddWithValue("day", day);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                (reader.GetString(0) == "F" ? disabled : features).Add(reader.GetString(1));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new TenantFunctionSettings(disabled, features);
    }
}
