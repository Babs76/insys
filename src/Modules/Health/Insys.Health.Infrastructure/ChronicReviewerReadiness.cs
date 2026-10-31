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
using Insys.Health.Infrastructure.Persistence;
using Insys.Identity.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.Health.Infrastructure;

/// <summary>Wave 5b §6, a warning: chronic conditions are listed and no active user holds the function that decides a registration (OQ-150).</summary>
internal sealed class ChronicReviewerReadiness([FromKeyedServices(HealthDbContext.SchemaName)] NpgsqlDataSource dataSource, IUserRoleQuery roles) : IReadinessCheck
{
    public const string DecideFunction = "HEALTH_APPROVE_CHRONIC_REGISTRATION";

    public IReadOnlyCollection<string> Steps { get; } = ["DOMAIN_REFERENCE_DATA"];

    public async Task<IReadOnlyList<ReadinessResult>> Check(ReadinessContext context, CancellationToken cancellationToken)
    {
        var listed = await ReadinessSql.InTenant(dataSource, context.TenantId, (connection, transaction) => ReadinessSql.Count(connection, transaction,
            $"SELECT count(*) FROM health.cfg_chronic_condition_list WHERE tenant_id = @t AND {ReadinessSql.InForce}", cancellationToken,
            ("t", context.TenantId), ("inForce", BuildingBlocks.Infrastructure.StaticData.StaticLifecycle.Configuration.InForce), ("day", context.Day)), cancellationToken);
        var unheld = listed > 0 && !await roles.AnyActiveHolder(context.TenantId, DecideFunction, context.Day, cancellationToken);
        return [ReadinessResult.Warning("DOMAIN_REFERENCE_DATA", "CHRONIC_REVIEWER", "Chronic conditions are listed and nobody can decide a registration", unheld ? [DecideFunction] : [])];
    }
}
