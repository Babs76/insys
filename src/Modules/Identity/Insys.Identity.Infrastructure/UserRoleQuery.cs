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
using Insys.Identity.Contracts;
using Insys.Identity.Domain.Users;
using Insys.Identity.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.Identity.Infrastructure;

/// <summary>The roles an active user holds today, read on Identity's own connection and role (ADR-001).</summary>
internal sealed class UserRoleQuery([FromKeyedServices(IdentityDbContext.SchemaName)] NpgsqlDataSource dataSource, TenantBusinessDates dates) : IUserRoleQuery
{
    public async Task<IReadOnlyList<string>> EffectiveRoles(Guid tenantId, Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT DISTINCT a.role_code
            FROM iam.user_role_assignment a
            JOIN iam.user_account u ON u.tenant_id = a.tenant_id AND u.id = a.user_id AND u.status = @active
            WHERE a.tenant_id = @t AND a.user_id = @id AND a.status = 'ACTIVE' AND a.valid_from <= @today AND (a.valid_to IS NULL OR a.valid_to > @today)
            ORDER BY a.role_code
            """, connection, transaction);
        command.Parameters.AddWithValue("active", UpperCaseEnumConverter<UserStatus>.ToCode(UserStatus.Active));
        command.Parameters.AddWithValue("t", tenantId);
        command.Parameters.AddWithValue("id", userId);
        command.Parameters.AddWithValue("today", dates.TodayFor(tenantId));
        var roles = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                roles.Add(reader.GetString(0));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return roles;
    }

    public async Task<bool> AnyActiveHolder(Guid tenantId, string functionCode, DateOnly day, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (
                SELECT 1 FROM iam.user_role_assignment a
                JOIN iam.user_account u ON u.tenant_id = a.tenant_id AND u.id = a.user_id AND u.status = @active
                JOIN iam.cfg_role_function_assignments g ON g.tenant_id = a.tenant_id AND g.role_code = a.role_code AND g.function_code = @function
                 AND g.record_status = ANY(@inForce) AND g.effective_from <= @day AND (g.effective_to IS NULL OR g.effective_to > @day)
                JOIN iam.cfg_roles_and_permission_sets r ON r.tenant_id = a.tenant_id AND r.role_code = a.role_code
                 AND r.record_status = ANY(@inForce) AND r.effective_from <= @day AND (r.effective_to IS NULL OR r.effective_to > @day)
                WHERE a.tenant_id = @t AND a.status = 'ACTIVE' AND a.valid_from <= @day AND (a.valid_to IS NULL OR a.valid_to > @day))
            """, connection, transaction);
        command.Parameters.AddWithValue("active", UpperCaseEnumConverter<UserStatus>.ToCode(UserStatus.Active));
        command.Parameters.AddWithValue("function", functionCode);
        command.Parameters.AddWithValue("inForce", BuildingBlocks.Infrastructure.StaticData.StaticLifecycle.Configuration.InForce);
        command.Parameters.AddWithValue("day", day);
        command.Parameters.AddWithValue("t", tenantId);
        var held = (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
        await transaction.CommitAsync(cancellationToken);
        return held;
    }
}
