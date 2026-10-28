/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Identity
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
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.Identity.Application.Users;
using Insys.Identity.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Insys.Identity.Infrastructure;

/// <summary>
/// BR-021: display names by user ID, on Identity's own connection, each tenant's users read under that tenant's row-level
/// security: the caller's tenant for names, the operator tenant only to tell which IDs are platform operators.
/// </summary>
internal sealed class DisplayNames([FromKeyedServices(IdentityDbContext.SchemaName)] NpgsqlDataSource dataSource, IOptions<PlatformOptions> platform) : IDisplayNames
{
    public async Task<IReadOnlyDictionary<Guid, string>> OfUsers(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        var names = new Dictionary<Guid, string>();
        await Read(tenantId, "SELECT id, display_name FROM iam.user_account WHERE tenant_id = @t AND id = ANY(@ids)", ids,
            reader => names[reader.GetGuid(0)] = reader.GetString(1), cancellationToken);
        return names;
    }

    public async Task<IReadOnlySet<Guid>> PlatformOperators(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        var operators = new HashSet<Guid>();
        await Read(platform.Value.OperatorTenantId, "SELECT id FROM iam.user_account WHERE tenant_id = @t AND id = ANY(@ids)", ids,
            reader => operators.Add(reader.GetGuid(0)), cancellationToken);
        return operators;
    }

    private async Task Read(Guid tenantId, string sql, IReadOnlyCollection<Guid> ids, Action<NpgsqlDataReader> row, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("t", tenantId);
        command.Parameters.AddWithValue("ids", ids.ToArray());
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                row(reader);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
