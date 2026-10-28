/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Infrastructure
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
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.BuildingBlocks.Infrastructure.Persistence;

/// <summary>A module's product version references (BR-030), read in the tenant by one query with <c>@code</c> and <c>@version</c> returning one text column.</summary>
public sealed class SqlProductVersionReferences(NpgsqlDataSource dataSource, string sql) : Contracts.IProductVersionReferences
{
    public async Task<IReadOnlyList<string>> Of(Guid tenantId, string productCode, int version, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("code", productCode);
        command.Parameters.AddWithValue("version", version);
        var references = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                references.Add(reader.GetString(0));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return references;
    }
}

public static class ProductVersionReferenceRegistration
{
    /// <summary>Registers a module's product version references (BR-030).</summary>
    public static IServiceCollection AddProductVersionReferences(this IServiceCollection services, string schema, string sql) =>
        services.AddSingleton<Contracts.IProductVersionReferences>(provider =>
            new SqlProductVersionReferences(provider.GetRequiredKeyedService<NpgsqlDataSource>(schema), sql));
}
