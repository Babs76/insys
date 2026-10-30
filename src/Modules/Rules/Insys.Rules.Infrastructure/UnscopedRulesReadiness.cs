/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Rules.Infrastructure
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
using Insys.Rules.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.Rules.Infrastructure;

/// <summary>
/// Onboarding readiness UNSCOPED_UNDERWRITING_RULES (PRODUCT_FACTORY), a warning, not a blocker (BR-113, decided 30 October
/// 2026): the acceptance and referral rules in force with neither a product nor a domain, which apply to every product's
/// underwriting decision. Some rules genuinely do; the tenant sees the list before go-live.
/// </summary>
internal sealed class UnscopedRulesReadiness([FromKeyedServices(RulesDbContext.SchemaName)] NpgsqlDataSource dataSource) : IReadinessCheck
{
    public IReadOnlyCollection<string> Steps { get; } = ["PRODUCT_FACTORY"];

    public async Task<IReadOnlyList<ReadinessResult>> Check(ReadinessContext context, CancellationToken cancellationToken)
    {
        var unscoped = await ReadinessSql.InTenant(dataSource, context.TenantId, (connection, transaction) => ReadinessSql.Strings(connection, transaction,
            $"""
            SELECT DISTINCT rule_code FROM rules.cfg_acceptance_and_referral_rules
            WHERE tenant_id = @t AND product_code IS NULL AND domain IS NULL AND {ReadinessSql.InForce} ORDER BY rule_code
            """, cancellationToken, ("t", context.TenantId), ("inForce", StaticLifecycle.Configuration.InForce), ("day", context.Day)), cancellationToken);
        return [ReadinessResult.Warning("PRODUCT_FACTORY", "UNSCOPED_UNDERWRITING_RULES",
            "Acceptance and referral rules in force with no product and no domain apply to every product (rule listed); a warning, not a blocker", unscoped)];
    }
}
