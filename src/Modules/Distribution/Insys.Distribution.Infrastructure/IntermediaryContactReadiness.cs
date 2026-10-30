/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Distribution
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
using Insys.BuildingBlocks.Infrastructure.Readiness;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Distribution.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.Distribution.Infrastructure;

/// <summary>
/// Onboarding readiness INTERMEDIARY_NO_CONTACT (PAYMENTS_AND_DISTRIBUTION), a warning, not a blocker (B-010, decided 30 October
/// 2026): active intermediaries with neither a contact party nor a party, who cannot be told of a broker of record change.
/// </summary>
internal sealed class IntermediaryContactReadiness([FromKeyedServices(DistributionDbContext.SchemaName)] NpgsqlDataSource dataSource) : IReadinessCheck
{
    public IReadOnlyCollection<string> Steps { get; } = ["PAYMENTS_AND_DISTRIBUTION"];

    public async Task<IReadOnlyList<ReadinessResult>> Check(ReadinessContext context, CancellationToken cancellationToken)
    {
        var unreachable = await ReadinessSql.InTenant(dataSource, context.TenantId, (connection, transaction) => ReadinessSql.Strings(connection, transaction,
            $"""
            SELECT DISTINCT intermediary_code FROM commission.mst_intermediary_register
            WHERE tenant_id = @t AND status = 'ACTIVE' AND contact_party_id IS NULL AND party_id IS NULL AND {ReadinessSql.InForce} ORDER BY intermediary_code
            """, cancellationToken, ("t", context.TenantId), ("inForce", StaticLifecycle.Reference.InForce), ("day", context.Day)), cancellationToken);
        return [ReadinessResult.Warning("PAYMENTS_AND_DISTRIBUTION", "INTERMEDIARY_NO_CONTACT",
            "Active intermediaries with no contact party and no party cannot be told of a broker of record change (intermediary listed); a warning, not a blocker", unreachable)];
    }
}
