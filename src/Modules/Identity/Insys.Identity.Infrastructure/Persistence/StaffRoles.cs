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

internal sealed class StaffRoles([FromKeyedServices(IdentityDbContext.SchemaName)] NpgsqlDataSource dataSource, FunctionCatalogueReader catalogue) : IStaffRoles, IPortalRoles
{
    /// <summary>The audiences of the portals (OQ-165): a function only these can hold is a portal function.</summary>
    public static readonly IReadOnlyList<string> PortalAudiences = ["CUSTOMER", "BROKER", "PROVIDER"];
    public async Task<IReadOnlyDictionary<string, IReadOnlySet<string>>> Permissions(Guid tenantId, DateOnly day, CancellationToken cancellationToken)
    {
        var functions = await catalogue.Functions(day, cancellationToken);
        return (await InForce(tenantId, day, cancellationToken)).ToDictionary(r => r.RoleCode,
            r => (IReadOnlySet<string>)r.Functions.Where(functions.ContainsKey).SelectMany(f => functions[f].Permissions).ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<(string RoleCode, IReadOnlyList<string> Functions)>> InForce(Guid tenantId, DateOnly day, CancellationToken cancellationToken)
    {
        var functions = await catalogue.Functions(day, cancellationToken);
        bool Portal(string function) => functions.TryGetValue(function, out var f) && f.Audiences.Count > 0 && f.Audiences.All(PortalAudiences.Contains);

        // OQ-165: a role that grants only portal functions isn't a staff role, even before anyone holds it.
        return (await Roles(tenantId, day, staffOnly: true, cancellationToken)).Where(r => r.Functions.Count == 0 || !r.Functions.All(Portal)).ToList();
    }

    async Task<IReadOnlyList<(string RoleCode, string Audience, IReadOnlyList<string> Functions)>> IPortalRoles.InForce(Guid tenantId, DateOnly day,
        CancellationToken cancellationToken)
    {
        var functions = await catalogue.Functions(day, cancellationToken);
        var portal = new List<(string, string, IReadOnlyList<string>)>();
        foreach (var (role, granted) in await Roles(tenantId, day, staffOnly: false, cancellationToken))
        {
            foreach (var audience in PortalAudiences)
            {
                var ofAudience = granted.Where(g => functions.TryGetValue(g, out var f) && f.Audiences.Contains(audience)).ToList();
                if (ofAudience.Count > 0)
                {
                    portal.Add((role, audience, ofAudience));
                }
            }
        }

        return portal;
    }

    private async Task<IReadOnlyList<(string RoleCode, IReadOnlyList<string> Functions)>> Roles(Guid tenantId, DateOnly day, bool staffOnly,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT r.role_code, ARRAY(SELECT g.function_code FROM iam.cfg_role_function_assignments g
                                      WHERE g.tenant_id = r.tenant_id AND g.role_code = r.role_code AND g.record_status = ANY(@inForce)
                                        AND g.effective_from <= @day AND (g.effective_to IS NULL OR g.effective_to > @day) ORDER BY g.function_code)
            FROM iam.cfg_roles_and_permission_sets r
            WHERE r.tenant_id = @t AND r.record_status = ANY(@inForce) AND r.effective_from <= @day AND (r.effective_to IS NULL OR r.effective_to > @day)
              AND (NOT @staffOnly OR NOT (EXISTS (SELECT 1 FROM iam.user_role_assignment a WHERE a.tenant_id = r.tenant_id AND a.role_code = r.role_code AND a.status = 'ACTIVE')
                   AND NOT EXISTS (SELECT 1 FROM iam.user_role_assignment a WHERE a.tenant_id = r.tenant_id AND a.role_code = r.role_code AND a.status = 'ACTIVE'
                                     AND NOT EXISTS (SELECT 1 FROM iam.user_subject_link l WHERE l.tenant_id = a.tenant_id AND l.user_id = a.user_id AND l.status = 'ACTIVE'))))
            ORDER BY r.role_code
            """, connection, transaction);
        command.Parameters.AddWithValue("t", tenantId);
        command.Parameters.AddWithValue("inForce", StaticLifecycle.Configuration.InForce);
        command.Parameters.AddWithValue("day", day);
        command.Parameters.AddWithValue("staffOnly", staffOnly);
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
