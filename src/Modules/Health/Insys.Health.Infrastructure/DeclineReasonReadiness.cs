/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Health
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
using Insys.ClaimsFramework.Contracts;
using Insys.Health.Application;
using Insys.Health.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.Health.Infrastructure;

/// <summary>BR-127: a tenant with health plans needs a DECLINE reason in force for each reason the system declines a claim with, or those claims can't be declined.</summary>
internal sealed class DeclineReasonReadiness([FromKeyedServices(HealthDbContext.SchemaName)] NpgsqlDataSource dataSource, IClaimDecisionReasons reasons) : IReadinessCheck
{
    public IReadOnlyCollection<string> Steps { get; } = ["DOMAIN_REFERENCE_DATA"];

    public async Task<IReadOnlyList<ReadinessResult>> Check(ReadinessContext context, CancellationToken cancellationToken)
    {
        var plans = await ReadinessSql.InTenant(dataSource, context.TenantId, (connection, transaction) => ReadinessSql.Count(connection, transaction,
            $"SELECT count(*) FROM {HealthCodeLists.PlansAndOptions} WHERE tenant_id = @t AND {ReadinessSql.InForce}", cancellationToken,
            ("t", context.TenantId), ("inForce", BuildingBlocks.Infrastructure.StaticData.StaticLifecycle.Configuration.InForce), ("day", context.Day)), cancellationToken);
        var missing = new List<string>();
        if (plans > 0)
        {
            foreach (var code in HealthDeclineRights.AutomaticReasons)
            {
                if (await reasons.InForce(context.TenantId, code, context.Day, cancellationToken) is not { DecisionType: ClaimDecisionTypes.Decline })
                {
                    missing.Add(code);
                }
            }
        }

        return [ReadinessResult.Of("DOMAIN_REFERENCE_DATA", "HEALTH_DECLINE_REASONS", "The reasons the system declines a Health claim with are in force", missing)];
    }
}
