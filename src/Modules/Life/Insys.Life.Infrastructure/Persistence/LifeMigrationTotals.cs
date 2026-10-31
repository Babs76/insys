/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Life
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
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.Life.Infrastructure.Persistence;

/// <summary>What this module loaded of a migration batch (B-013).</summary>
internal sealed class LifeMigrationTotals([FromKeyedServices(LifeDbContext.SchemaName)] NpgsqlDataSource dataSource) : IMigrationTotals
{
    public Task<IReadOnlyList<MigrationTotal>> For(Guid tenantId, string batchCode, CancellationToken cancellationToken) =>
        MigrationTotalsReader.Read(dataSource, tenantId, batchCode, """
        SELECT 'LIFE_POLICY', count(DISTINCT p.id), sum(b.premium) FROM life.policy p JOIN life.policy_benefit b ON b.tenant_id = p.tenant_id AND b.policy_id = p.id WHERE p.migration_batch = @batch HAVING count(*) > 0
        UNION ALL SELECT 'LIFE_CLAIM', count(*), NULL::numeric FROM life.claim WHERE migration_batch = @batch HAVING count(*) > 0
        """, cancellationToken);
}

/// <summary>This module's policies, for an approved export (OQ-158).</summary>
internal sealed class LifePolicyExport([FromKeyedServices(LifeDbContext.SchemaName)] NpgsqlDataSource dataSource) : IPolicyExportRows
{
    public string Domain => "LIFE";

    public Task<IReadOnlyList<PolicyExportRow>> All(Guid tenantId, CancellationToken cancellationToken) =>
        PolicyExportReader.Read(dataSource, tenantId, Domain, "SELECT policy_number, product_code, policyholder_party_id, status, inception_date, term_end_date, currency FROM life.policy ORDER BY id", cancellationToken);
}
