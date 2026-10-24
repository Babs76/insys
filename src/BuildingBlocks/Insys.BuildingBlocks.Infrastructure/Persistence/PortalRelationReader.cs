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
using Insys.BuildingBlocks.Application.Context;
using Npgsql;
using NpgsqlTypes;

namespace Insys.BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// Runs a module's relationship query (external-user access §2) in the tenant, under row-level security. The query gets
/// <c>@id</c>, <c>@party</c> (the member's party, or null), <c>@codes</c> (the broker's intermediary codes, or empty) and
/// <c>@provider</c> (the provider, or null), so one predicate serves every kind of external user.
/// </summary>
public static class PortalRelationReader
{
    public static async Task<bool> Exists(NpgsqlDataSource dataSource, Guid tenantId, string sql, Guid id, ExternalSubject who, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand($"SELECT EXISTS ({sql})", connection, transaction);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.Add(new NpgsqlParameter("party", NpgsqlDbType.Uuid) { Value = who.Kind == ExternalSubject.Party && who.PartyId is { } p ? p : DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("codes", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = who.Kind == ExternalSubject.Intermediary ? who.IntermediaryCodes.ToArray() : Array.Empty<string>(),
        });
        command.Parameters.Add(new NpgsqlParameter("provider", NpgsqlDbType.Text) { Value = who.Kind == ExternalSubject.Provider ? who.ProviderId ?? (object)DBNull.Value : DBNull.Value });
        var related = (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
        await transaction.CommitAsync(cancellationToken);
        return related;
    }

    /// <param name="sql">A query with <c>@codes</c> returning one policy ID per row; with <c>@id</c> too, it is used for one policy.</param>
    public static async Task<IReadOnlyList<Guid>> Ids(NpgsqlDataSource dataSource, Guid tenantId, string sql, IReadOnlyCollection<string> codes, Guid? id,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter("codes", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = codes.ToArray() });
        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = id is { } value ? value : DBNull.Value });
        var ids = new List<Guid>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                ids.Add(reader.GetGuid(0));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return ids;
    }
}
