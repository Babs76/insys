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
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Identity.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.Identity.Infrastructure.Persistence;

internal sealed class StaffRoles([FromKeyedServices(IdentityDbContext.SchemaName)] NpgsqlDataSource dataSource) : IStaffRoles
{
    public async Task<IReadOnlyList<(string RoleCode, IReadOnlyList<string> Permissions)>> InForce(Guid tenantId, DateOnly day, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT r.role_code, r.permissions FROM iam.cfg_roles_and_permission_sets r
            WHERE r.tenant_id = @t AND r.record_status = ANY(@inForce) AND r.effective_from <= @day AND (r.effective_to IS NULL OR r.effective_to > @day)
              AND NOT (EXISTS (SELECT 1 FROM iam.user_role_assignment a WHERE a.tenant_id = r.tenant_id AND a.role_code = r.role_code AND a.status = 'ACTIVE')
                   AND NOT EXISTS (SELECT 1 FROM iam.user_role_assignment a WHERE a.tenant_id = r.tenant_id AND a.role_code = r.role_code AND a.status = 'ACTIVE'
                                     AND NOT EXISTS (SELECT 1 FROM iam.user_subject_link l WHERE l.tenant_id = a.tenant_id AND l.user_id = a.user_id AND l.status = 'ACTIVE')))
            ORDER BY r.role_code
            """, connection, transaction);
        command.Parameters.AddWithValue("t", tenantId);
        command.Parameters.AddWithValue("inForce", StaticLifecycle.Configuration.InForce);
        command.Parameters.AddWithValue("day", day);
        var roles = new List<(string, IReadOnlyList<string>)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                roles.Add((reader.GetString(0), reader.GetFieldValue<string[]>(1)));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return roles;
    }
}
