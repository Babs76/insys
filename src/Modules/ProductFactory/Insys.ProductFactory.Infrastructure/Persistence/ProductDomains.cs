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
using Insys.ProductFactory.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.ProductFactory.Infrastructure.Persistence;

/// <summary>Each product code's domain, by its latest version in any status, read on the Product Factory's own connection.</summary>
internal sealed class ProductDomains([FromKeyedServices(ProductFactoryDbContext.SchemaName)] NpgsqlDataSource dataSource) : IProductDomains
{
    public async Task<IReadOnlyDictionary<string, string>> DomainsOf(Guid tenantId, IReadOnlyCollection<string> productCodes, CancellationToken cancellationToken)
    {
        var domains = new Dictionary<string, string>(StringComparer.Ordinal);
        if (productCodes.Count == 0)
        {
            return domains;
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT DISTINCT ON (product_code) product_code, domain FROM product.cfg_product_definitions_and_versions
            WHERE (tenant_id = @t OR tenant_id IS NULL) AND product_code = ANY(@codes) ORDER BY product_code, version DESC
            """, connection, transaction);
        command.Parameters.AddWithValue("t", tenantId);
        command.Parameters.AddWithValue("codes", productCodes.ToArray());
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                domains[reader.GetString(0)] = reader.GetString(1);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return domains;
    }
}
