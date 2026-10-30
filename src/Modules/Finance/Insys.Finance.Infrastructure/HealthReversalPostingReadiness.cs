/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Finance
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
using Insys.Finance.Contracts;
using Insys.Finance.Infrastructure.Persistence;
using Insys.Tenancy.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.Finance.Infrastructure;

/// <summary>Onboarding readiness POSTING_RULES: a tenant entitled to HEALTH needs a rule for a reversed claim, or each reversal waits as a posting exception.</summary>
internal sealed class HealthReversalPostingReadiness([FromKeyedServices(FinanceDbContext.SchemaName)] NpgsqlDataSource dataSource, ITenantFunctionSettings settings) : IReadinessCheck
{
    public IReadOnlyCollection<string> Steps { get; } = ["FINANCIAL_FOUNDATIONS"];

    public async Task<IReadOnlyList<ReadinessResult>> Check(ReadinessContext context, CancellationToken cancellationToken)
    {
        if (!(await settings.For(context.TenantId, context.Day, cancellationToken)).EntitledFeatures.Contains("HEALTH"))
        {
            return [];
        }

        var rules = await ReadinessSql.InTenant(dataSource, context.TenantId, (connection, transaction) => ReadinessSql.Count(connection, transaction,
            $"SELECT count(*) FROM finance.cfg_posting_rules WHERE tenant_id = @t AND transaction_type = @type AND {ReadinessSql.InForce}", cancellationToken,
            ("t", context.TenantId), ("type", TransactionTypes.ClaimPaidReversed), ("inForce", StaticLifecycle.Configuration.InForce), ("day", context.Day)), cancellationToken);
        return [ReadinessResult.Of("FINANCIAL_FOUNDATIONS", "POSTING_RULES", "A posting rule for premium due, payment received, refund paid, claim reserve up and down, and claim paid",
            rules > 0 ? [] : [TransactionTypes.ClaimPaidReversed])];
    }
}
