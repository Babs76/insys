/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : ProductFactory
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
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.ProductFactory.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.ProductFactory.Infrastructure.Persistence;

internal sealed class SellableProducts([FromKeyedServices(ProductFactoryDbContext.SchemaName)] NpgsqlDataSource dataSource) : ISellableProducts, IPublishedProducts
{
    public async Task<IReadOnlyList<(string ProductCode, int Version)>> Sellable(Guid tenantId, DateOnly day, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> codes;
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
            codes = await BuildingBlocks.Infrastructure.Readiness.ReadinessSql.Strings(connection, transaction, $"""
                SELECT DISTINCT code FROM product.cfg_product_definitions_and_versions
                WHERE tenant_id = @t AND {BuildingBlocks.Infrastructure.Readiness.ReadinessSql.InForce} ORDER BY code
                """, cancellationToken, ("t", tenantId), ("inForce", Sold), ("day", day));
            await transaction.CommitAsync(cancellationToken);
        }

        var sellable = new List<(string, int)>();
        foreach (var code in codes)
        {
            if (await Sellable(tenantId, code, null, day, cancellationToken) is { } product)
            {
                sellable.Add((code, product.Version));
            }
        }

        return sellable;
    }

    private static readonly string[] Sold = ["PUBLISHED", "EFFECTIVE"];
    private static readonly string[] Issued = ["PUBLISHED", "EFFECTIVE", "WITHDRAWN", "RETIRED"];

    public Task<SellableProduct?> Sellable(Guid tenantId, string productCode, int? version, DateOnly day, CancellationToken cancellationToken) =>
        Read(tenantId, productCode, version, day, Sold, checkSaleWindow: true, cancellationToken);

    public Task<SellableProduct?> Version(Guid tenantId, string productCode, int version, DateOnly day, CancellationToken cancellationToken) =>
        Read(tenantId, productCode, version, day, Issued, checkSaleWindow: false, cancellationToken);

    public async Task<IReadOnlyList<ProductTemplate>> Templates(Guid tenantId, string productCode, DateOnly day, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT document_type, template, coalesce(language, 'en') FROM product.cfg_product_template_assignments
            WHERE (tenant_id = @t OR tenant_id IS NULL) AND product_code = @c AND record_status = ANY(@statuses)
              AND effective_from <= @day AND (effective_to IS NULL OR effective_to > @day)
            ORDER BY document_type, template
            """, connection, transaction);
        command.Parameters.AddWithValue("t", tenantId);
        command.Parameters.AddWithValue("c", productCode);
        command.Parameters.AddWithValue("statuses", StaticLifecycle.Configuration.InForce);
        command.Parameters.AddWithValue("day", day);
        var templates = new List<ProductTemplate>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                templates.Add(new ProductTemplate(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return templates;
    }

    private async Task<SellableProduct?> Read(Guid tenantId, string productCode, int? version, DateOnly day, string[] statuses, bool checkSaleWindow,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT p.version, p.domain, coalesce(p.class_code, p.family_code, p.domain), p.coverages, p.cover_starts_on, p.eligibility_ruleset_code, p.reinstatement_arrears
            FROM product.cfg_product_definitions_and_versions p
            WHERE p.tenant_id = @t AND p.code = @c AND p.record_status = ANY(@statuses) AND (@version::int IS NULL OR p.version = @version)
              AND (NOT @sold OR (p.effective_from <= @day AND (p.effective_to IS NULL OR p.effective_to > @day)))
              AND (NOT @sold OR NOT EXISTS (SELECT 1 FROM product.cfg_product_availability_rules a
                       WHERE (a.tenant_id = @t OR a.tenant_id IS NULL) AND a.product_code = @c AND a.record_status = ANY(@config)
                         AND a.effective_from <= @day AND (a.effective_to IS NULL OR a.effective_to > @day))
                   OR EXISTS (SELECT 1 FROM product.cfg_product_availability_rules a
                       WHERE (a.tenant_id = @t OR a.tenant_id IS NULL) AND a.product_code = @c AND a.record_status = ANY(@config)
                         AND a.effective_from <= @day AND (a.effective_to IS NULL OR a.effective_to > @day)
                         AND a.sale_from <= @day AND (a.sale_to IS NULL OR a.sale_to >= @day)))
            ORDER BY p.version DESC LIMIT 1
            """, connection, transaction);
        command.Parameters.AddWithValue("t", tenantId);
        command.Parameters.AddWithValue("c", productCode);
        command.Parameters.AddWithValue("statuses", statuses);
        command.Parameters.AddWithValue("config", StaticLifecycle.Configuration.InForce);
        command.Parameters.Add(new NpgsqlParameter<int?>("version", NpgsqlTypes.NpgsqlDbType.Integer) { TypedValue = version });
        command.Parameters.AddWithValue("sold", checkSaleWindow);
        command.Parameters.AddWithValue("day", day);
        (int Version, string Domain, string Line, string[] Coverages, string CoverStartsOn, string? Ruleset, string ReinstatementArrears)? product = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                product = (reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetFieldValue<string[]>(3), reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetString(6));
            }
        }

        if (product is not { } found)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        await using var limitsCommand = new NpgsqlCommand("""
            SELECT schedule_code, coverage, limit_type, amount, percentage, rule, currency FROM product.cfg_limit_and_deductible_schedules
            WHERE (tenant_id = @t OR tenant_id IS NULL) AND coverage = ANY(@coverages) AND record_status = ANY(@config)
              AND effective_from <= @day AND (effective_to IS NULL OR effective_to > @day)
            ORDER BY schedule_code, coverage, limit_type
            """, connection, transaction);
        limitsCommand.Parameters.AddWithValue("t", tenantId);
        limitsCommand.Parameters.AddWithValue("coverages", found.Coverages);
        limitsCommand.Parameters.AddWithValue("config", StaticLifecycle.Configuration.InForce);
        limitsCommand.Parameters.AddWithValue("day", day);
        var limits = new List<ProductLimit>();
        await using (var reader = await limitsCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                limits.Add(new ProductLimit(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetDecimal(3),
                    reader.IsDBNull(4) ? null : reader.GetDecimal(4), reader.GetString(5), reader.GetString(6)));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new SellableProduct(productCode, found.Version, found.Domain, found.Line, found.Coverages, limits, found.CoverStartsOn, found.Ruleset, found.ReinstatementArrears);
    }
}
