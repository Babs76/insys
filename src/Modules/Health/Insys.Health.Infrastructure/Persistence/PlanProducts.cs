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
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Health.Domain;
using Insys.Health.Infrastructure.StaticData;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Insys.Health.Infrastructure.Persistence;

/// <summary>
/// A plan option names its product, and every option of a plan names the same one (addenda/health-plan-products.md §1),
/// checked when a version is submitted. Whether the product is a Health product on sale is checked at enrolment, since a
/// tenant sets up its plans before it publishes its products (onboarding order).
/// </summary>
internal sealed class PlanProductsGuard : IStaticSetGuard
{
    public string Schema => "health";

    public string Table => "cfg_plans_and_options";

    public Result Check(StaticRecord proposed, IReadOnlyList<StaticRecord> others)
    {
        if (proposed is not PlansAndOptions option)
        {
            return Result.Success();
        }

        if (string.IsNullOrWhiteSpace(option.ProductCode))
        {
            return Error.Validation(HealthErrorCodes.PlanProductInvalid, $"Plan option {option.PlanCode}/{option.OptionCode} names no product; a new version must name the Health product it is sold as.");
        }

        return others.OfType<PlansAndOptions>().FirstOrDefault(o => o.Code != option.Code && o.PlanCode == option.PlanCode && o.ProductCode is not null && o.ProductCode != option.ProductCode)
            is { } other
            ? Error.Validation(HealthErrorCodes.PlanProductInvalid,
                $"Plan {option.PlanCode}'s option {other.OptionCode} is sold as {other.ProductCode}; every option of a plan names the same product (options are its tiers).")
            : Result.Success();
    }
}

/// <summary>
/// /health "health-plan-products": Degraded while any tenant has a plan option in force that names no product. Its
/// memberships have no product, so product-scoped users see none of them (OQ-080 fail-safe), until a new version names one.
/// </summary>
internal sealed class PlanProductsHealthCheck([FromKeyedServices(HealthDbContext.SchemaName)] NpgsqlDataSource dataSource, ITenantDirectory tenants) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var statuses = HealthStaticData.Descriptors.Single(d => d.EntityType == typeof(PlansAndOptions)).Lifecycle.InForce;
        var found = new List<string>();
        foreach (var tenantId in await tenants.GetActiveTenantIds(cancellationToken))
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
            await using var command = new NpgsqlCommand("""
                SELECT string_agg(DISTINCT plan_code, ',') FROM health.cfg_plans_and_options
                WHERE tenant_id = @t AND product_code IS NULL AND record_status = ANY(@s) AND effective_from <= CURRENT_DATE AND (effective_to IS NULL OR effective_to > CURRENT_DATE)
                """, connection, transaction);
            command.Parameters.AddWithValue("t", tenantId);
            command.Parameters.AddWithValue("s", statuses);
            if (await command.ExecuteScalarAsync(cancellationToken) is string plans)
            {
                found.Add($"{tenantId}: {plans}");
            }

            await transaction.CommitAsync(cancellationToken);
        }

        return found.Count == 0
            ? HealthCheckResult.Healthy("Every Health plan in force names its product.")
            : HealthCheckResult.Degraded($"{found.Count} tenant(s) have Health plans with no product (OQ-080): {string.Join("; ", found.Take(20))}",
                data: new Dictionary<string, object> { ["plansWithoutProduct"] = found });
    }
}
