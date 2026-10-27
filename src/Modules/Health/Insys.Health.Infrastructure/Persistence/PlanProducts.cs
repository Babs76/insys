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

        // productCode is mandatory in the catalogue (BR-084), so the input refuses a draft without it; this covers drafts saved before.
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
/// /health "health-plan-products": Degraded while any tenant has a plan option in force whose product is missing, is not
/// a product the tenant has defined, is not a Health product, or is not the one its plan's other options name
/// (addenda/health-plan-products.md §1, §4). Until fixed, its memberships are outside product scopes or wrongly scoped.
/// </summary>
internal sealed class PlanProductsHealthCheck([FromKeyedServices(HealthDbContext.SchemaName)] NpgsqlDataSource dataSource, ITenantDirectory tenants,
    ProductFactory.Contracts.IProductDomains products, TenantBusinessDates dates) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var statuses = HealthStaticData.Descriptors.Single(d => d.EntityType == typeof(PlansAndOptions)).Lifecycle.InForce;
        var found = new List<string>();
        foreach (var tenantId in await tenants.GetActiveTenantIds(cancellationToken))
        {
            var options = new List<(string Plan, string Option, string? Product)>();
            await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
            await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
            {
                await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
                await using var command = new NpgsqlCommand("""
                    SELECT DISTINCT ON (plan_code, option_code) plan_code, option_code, product_code FROM health.cfg_plans_and_options
                    WHERE tenant_id = @t AND record_status = ANY(@s) AND effective_from <= @day AND (effective_to IS NULL OR effective_to > @day)
                    ORDER BY plan_code, option_code, version DESC
                    """, connection, transaction);
                command.Parameters.AddWithValue("t", tenantId);
                command.Parameters.AddWithValue("s", statuses);
                command.Parameters.AddWithValue("day", dates.TodayFor(tenantId));
                await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                {
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        options.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
                    }
                }

                await transaction.CommitAsync(cancellationToken);
            }

            var domains = await products.DomainsOf(tenantId, options.Where(o => o.Product is not null).Select(o => o.Product!).Distinct().ToList(), cancellationToken);
            found.AddRange(Problems(options, domains).Select(p => $"{tenantId}: {p}"));
        }

        return found.Count == 0
            ? HealthCheckResult.Healthy("Every Health plan in force names one Health product.")
            : HealthCheckResult.Degraded($"{found.Count} Health plan option(s) need their product fixed (addenda/health-plan-products.md): {string.Join("; ", found.Take(20))}",
                data: new Dictionary<string, object> { ["planOptions"] = found });
    }

    /// <summary>What is wrong with each plan option's product, if anything.</summary>
    internal static IEnumerable<string> Problems(IReadOnlyList<(string Plan, string Option, string? Product)> options, IReadOnlyDictionary<string, string> domains)
    {
        foreach (var plan in options.GroupBy(o => o.Plan).OrderBy(g => g.Key))
        {
            var named = plan.Where(o => o.Product is not null).Select(o => o.Product!).Distinct().ToList();
            foreach (var option in plan.OrderBy(o => o.Option))
            {
                var where = $"{option.Plan}/{option.Option}";
                if (option.Product is null)
                {
                    yield return $"{where} names no product";
                }
                else if (!domains.TryGetValue(option.Product, out var domain))
                {
                    yield return $"{where} names {option.Product}, which is not a product";
                }
                else if (domain != "HEALTH")
                {
                    yield return $"{where} names {option.Product}, a {domain} product";
                }
            }

            if (named.Count > 1)
            {
                yield return $"{plan.Key}'s options name different products ({string.Join(", ", named)})";
            }
        }
    }
}
