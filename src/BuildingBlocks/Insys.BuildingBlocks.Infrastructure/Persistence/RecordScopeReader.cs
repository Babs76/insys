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

/// <summary>
/// Reads one record's product and organisation node on the module's own connection, in the tenant (F103): for a module
/// telling Workflow the scope of the record an approval is about.
/// </summary>
public static class RecordScopeReader
{
    /// <param name="sql">A query with the parameter <c>@id</c>, returning the product and the node (either may be null), or no row.</param>
    public static async Task<(string? Product, string? OrgNode)?> Read(NpgsqlDataSource dataSource, Guid tenantId, string sql, Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        (string?, string?)? scope = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                scope = (reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return scope;
    }

    /// <param name="sql">A query with the parameter <c>@id</c>, returning one row of product and node for each part of the record.</param>
    public static async Task<IReadOnlyList<(string? Product, string? OrgNode)>> ReadAll(NpgsqlDataSource dataSource, Guid tenantId, string sql, Guid id,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        var scopes = new List<(string?, string?)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                scopes.Add((reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return scopes;
    }
}

/// <summary>
/// A module's legal hold targets (D-34) as one query per scope type, on the module's own connection: each returns the
/// record's product and node, or no row when the record does not exist.
/// </summary>
public sealed class SqlLegalHoldTargets(NpgsqlDataSource dataSource, IReadOnlyDictionary<string, string> sqlByScope) : Contracts.ILegalHoldTargets
{
    public bool Covers(string scopeType) => sqlByScope.ContainsKey(scopeType);

    public async Task<IReadOnlyList<(string? ProductCode, string? OrgNodeCode)>?> ScopesOf(Guid tenantId, string scopeType, Guid recordId, CancellationToken cancellationToken)
    {
        if (!sqlByScope.TryGetValue(scopeType, out var sql) || await RecordScopeReader.Read(dataSource, tenantId, sql, recordId, cancellationToken) is not { } scope)
        {
            return null;
        }

        return scope.Product is null && scope.OrgNode is null ? [] : [(scope.Product, scope.OrgNode)];
    }
}

public static class LegalHoldTargetRegistration
{
    /// <summary>Registers a module's legal hold targets (D-34): one query per scope type, with the parameter <c>@id</c>, returning the record's product and node.</summary>
    public static IServiceCollection AddLegalHoldTargets(this IServiceCollection services, string schema, IReadOnlyDictionary<string, string> sqlByScope) =>
        services.AddSingleton<Contracts.ILegalHoldTargets>(provider => new SqlLegalHoldTargets(provider.GetRequiredKeyedService<NpgsqlDataSource>(schema), sqlByScope));
}
