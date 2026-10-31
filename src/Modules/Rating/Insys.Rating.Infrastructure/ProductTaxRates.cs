/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Rating
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
using Insys.BuildingBlocks.Infrastructure.Readiness;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Rating.Contracts;
using Insys.Rating.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.Rating.Infrastructure;

/// <summary>Runs on Rating's own connection. A product has one tax on its invoices; were two in force, the lowest code applies and readiness warns.</summary>
internal sealed class ProductTaxRates([FromKeyedServices(RatingDbContext.SchemaName)] NpgsqlDataSource dataSource) : IProductTaxRates
{
    public Task<ProductTaxRate?> ForProduct(Guid tenantId, string productCode, DateOnly day, CancellationToken cancellationToken) =>
        ReadinessSql.InTenant(dataSource, tenantId, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand(
                $"SELECT tax_code, rate FROM rating.cfg_premium_tax_and_levy_rates WHERE tenant_id = @t AND product_code = @p AND {ReadinessSql.InForce} ORDER BY tax_code LIMIT 1", connection, transaction);
            command.Parameters.AddWithValue("t", tenantId);
            command.Parameters.AddWithValue("p", productCode);
            command.Parameters.AddWithValue("inForce", StaticLifecycle.Configuration.InForce);
            command.Parameters.AddWithValue("day", day);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? new ProductTaxRate(reader.GetString(0), reader.GetDecimal(1)) : null;
        }, cancellationToken);
}
