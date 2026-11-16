/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : StaticData
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
using Insys.BuildingBlocks.Application;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.BuildingBlocks.Infrastructure.Readiness;
using Insys.StaticData.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.StaticData.Infrastructure;

/// <summary>
/// Onboarding readiness SETUP_CHANGE_SET (END_TO_END_TESTS), a blocker (AIA-086, option C): no SETUP change set waiting for its approval or partly applied,
/// and no submission whose batches are loaded but whose set isn't recorded yet (LOADING).
/// The tenant can't go live while one is open; activation refuses it too, whenever the step was completed.
/// </summary>
internal sealed class SetupChangeSetReadiness([FromKeyedServices(StaticDataDbContext.SchemaName)] NpgsqlDataSource dataSource) : IReadinessCheck
{
    public const string Step = "END_TO_END_TESTS";
    public const string Code = "SETUP_CHANGE_SET";

    public IReadOnlyCollection<string> Steps { get; } = [Step];

    public async Task<IReadOnlyList<ReadinessResult>> Check(ReadinessContext context, CancellationToken cancellationToken)
    {
        var open = await ReadinessSql.InTenant(dataSource, context.TenantId, (connection, transaction) => ReadinessSql.Strings(connection, transaction,
            """
            SELECT set_number || ' ' || status FROM sdm.setup_change_set WHERE tenant_id = @t AND status IN ('SUBMITTED', 'APPLYING')
            UNION ALL
            SELECT DISTINCT 'SC-' || setup_set_id::text || ' LOADING' FROM sdm.change_request c
            WHERE c.tenant_id = @t AND c.setup_set_id IS NOT NULL AND c.status = 'PENDING_APPROVAL'
              AND NOT EXISTS (SELECT 1 FROM sdm.setup_change_set s WHERE s.tenant_id = c.tenant_id AND s.id = c.setup_set_id)
            ORDER BY 1
            """, cancellationToken, ("t", context.TenantId)), cancellationToken);
        return [ReadinessResult.Of(Step, Code,
            "No SETUP change set waiting for its approval or partly applied: the tenant's setup is applied in full before go-live (AIA-086)", open)];
    }
}

/// <summary>Public query interface ISetupChangeSetState, on Static Data's own connection.</summary>
internal sealed class SetupChangeSetStateQuery([FromKeyedServices(StaticDataDbContext.SchemaName)] NpgsqlDataSource dataSource) : Contracts.ISetupChangeSetState
{
    public async Task<Contracts.SetupChangeSetState> For(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM sdm.setup_change_set WHERE tenant_id = @t AND status IN ('SUBMITTED', 'APPLYING'))
                 + (SELECT count(DISTINCT c.setup_set_id) FROM sdm.change_request c
                    WHERE c.tenant_id = @t AND c.setup_set_id IS NOT NULL AND c.status = 'PENDING_APPROVAL'
                      AND NOT EXISTS (SELECT 1 FROM sdm.setup_change_set s WHERE s.tenant_id = c.tenant_id AND s.id = c.setup_set_id)),
                   (SELECT count(*) FROM sdm.setup_change_set WHERE tenant_id = @t AND status = 'APPLIED'),
                   (SELECT max(applied_at) FROM sdm.setup_change_set WHERE tenant_id = @t)
            """, connection, transaction);
        command.Parameters.AddWithValue("t", tenantId);
        Contracts.SetupChangeSetState state;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            state = new Contracts.SetupChangeSetState((int)reader.GetInt64(0), (int)reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2));
        }

        await transaction.CommitAsync(cancellationToken);
        return state;
    }
}
