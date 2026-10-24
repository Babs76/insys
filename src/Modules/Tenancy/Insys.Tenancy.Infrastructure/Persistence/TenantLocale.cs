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
using Insys.BuildingBlocks.Infrastructure.Readiness;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Tenancy.Application.Tenants;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.Tenancy.Infrastructure.Persistence;

internal sealed class TenantLocale([FromKeyedServices(TenancyDbContext.SchemaName)] NpgsqlDataSource dataSource) : ITenantLocale
{
    public Task<(string? BaseCurrency, string? DefaultLanguage)> For(Guid tenantId, DateOnly day, CancellationToken cancellationToken) =>
        ReadinessSql.InTenant(dataSource, tenantId, async (connection, transaction) =>
        {
            var row = await ReadinessSql.Strings(connection, transaction, $"""
                SELECT coalesce(trim(base_currency), '') || ':' || coalesce(trim(default_language), '') FROM tenant.cfg_locale_defaults
                WHERE tenant_id = @t AND {ReadinessSql.InForce} ORDER BY version DESC LIMIT 1
                """, cancellationToken, ("t", tenantId), ("inForce", StaticLifecycle.Configuration.InForce), ("day", day));
            if (row.Count == 0)
            {
                return ((string?)null, (string?)null);
            }

            var parts = row[0].Split(':');
            return (parts[0].Length > 0 ? parts[0] : null, parts[1].Length > 0 ? parts[1] : null);
        }, cancellationToken);
}
