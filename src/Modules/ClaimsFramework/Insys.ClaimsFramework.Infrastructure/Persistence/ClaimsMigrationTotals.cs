/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : ClaimsFramework
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

namespace Insys.ClaimsFramework.Infrastructure.Persistence;

/// <summary>The reserves a migration batch brought (B-013).</summary>
internal sealed class ClaimsMigrationTotals([FromKeyedServices(ClaimsFrameworkDbContext.SchemaName)] NpgsqlDataSource dataSource) : IMigrationTotals
{
    public Task<IReadOnlyList<MigrationTotal>> For(Guid tenantId, string batchCode, CancellationToken cancellationToken) =>
        MigrationTotalsReader.Read(dataSource, tenantId, batchCode, """
        SELECT 'CLAIM_RESERVE', count(*), sum(m.amount) FROM claims.claim_financial_movement m JOIN claims.claim_register r ON r.tenant_id = m.tenant_id AND r.claim_number = m.claim_number
        WHERE r.migration_batch = @batch HAVING count(*) > 0
        """, cancellationToken);
}
