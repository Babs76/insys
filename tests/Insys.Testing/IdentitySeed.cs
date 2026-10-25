/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Testing
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
namespace Insys.Testing;

/// <summary>
/// Gives a subject permissions the way production does: an active iam.user_account, a role version in force in
/// iam.cfg_roles_and_permission_sets and an effective assignment. Seeded as the owner, outside maker-checker.
/// </summary>
public static class IdentitySeed
{
    /// <summary>Creates the user (or reuses it) and assigns a new role holding the permissions. Returns the user ID.</summary>
    public static async Task<Guid> UserWithPermissions(this TestDatabase database, Guid tenantId, string subject, params string[] permissions)
    {
        var userId = await database.ScalarAsOwner<Guid?>("SELECT id FROM iam.user_account WHERE tenant_id = @t AND external_subject = @s",
            ("t", tenantId), ("s", subject)) ?? Guid.CreateVersion7();
        await database.ExecuteAsOwner("""
            INSERT INTO iam.user_account (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by,
                                          username, display_name, idp_code, external_subject, status, mfa_enrolled)
            VALUES (@t, @id, 1, now(), 'seed', now(), 'seed', @s, @s, 'TEST', @s, 'ACTIVE', true)
            ON CONFLICT DO NOTHING
            """, ("t", tenantId), ("id", userId), ("s", subject));
        if (permissions.Length > 0)
        {
            var role = await database.RoleWithPermissions(tenantId, permissions);
            await database.AssignRole(tenantId, userId, role);
        }

        return userId;
    }

    /// <summary>
    /// A role version in force today that grants the functions needing only these permissions (ADR-026 §7), so the
    /// user holds exactly them. Fails when a permission is needed only by functions that need more: say which. A permission
    /// nothing checks (no function needs it) gives nothing, before or after, and is ignored. Returns the role code.
    /// </summary>
    public static async Task<string> RoleWithPermissions(this TestDatabase database, Guid tenantId, params string[] permissions)
    {
        var role = "R_" + Guid.NewGuid().ToString("N")[..20].ToUpperInvariant();
        await database.ExecuteAsOwner("""
            INSERT INTO iam.cfg_roles_and_permission_sets (tenant_id, code, version, effective_from, record_status, created_by, change_reason, role_code)
            VALUES (@t, @role, 1, current_date - 1, 'EFFECTIVE', 'seed', 'test seed', @role)
            """, ("t", tenantId), ("role", role));
        await database.ExecuteAsOwner("""
            INSERT INTO iam.cfg_role_function_assignments (tenant_id, code, version, effective_from, record_status, created_by, change_reason, role_code, function_code)
            SELECT @t, @role || ':' || f, 1, current_date - 1, 'EFFECTIVE', 'seed', 'test seed', @role, f FROM iam.functions_within(@permissions) AS f
            """, ("t", tenantId), ("role", role), ("permissions", permissions));
        var missing = await database.ScalarAsOwner<string>("""
            SELECT string_agg(p, ', ') FROM unnest(@permissions::text[]) AS p
            WHERE EXISTS (SELECT 1 FROM iam.ref_function_catalogue k WHERE k.tenant_id IS NULL AND k.record_status = 'ACTIVE' AND k.effective_to IS NULL
                          AND p = ANY(k.required_permissions))
              AND NOT EXISTS (SELECT 1 FROM iam.functions_within(@permissions) AS f JOIN iam.ref_function_catalogue c ON c.function_code = f
                              WHERE c.tenant_id IS NULL AND c.record_status = 'ACTIVE' AND c.effective_to IS NULL AND p = ANY(c.required_permissions))
            """, ("permissions", permissions));
        return missing is null ? role
            : throw new InvalidOperationException($"No function needs only these permissions, so a role cannot hold {missing}: add the permissions its function needs (ADR-026).");
    }

    /// <summary>A role version in force today that grants these functions. Returns the role code.</summary>
    public static async Task<string> RoleWithFunctions(this TestDatabase database, Guid tenantId, params string[] functions)
    {
        var role = "R_" + Guid.NewGuid().ToString("N")[..20].ToUpperInvariant();
        await database.ExecuteAsOwner("""
            INSERT INTO iam.cfg_roles_and_permission_sets (tenant_id, code, version, effective_from, record_status, created_by, change_reason, role_code)
            VALUES (@t, @role, 1, current_date - 1, 'EFFECTIVE', 'seed', 'test seed', @role)
            """, ("t", tenantId), ("role", role));
        await database.ExecuteAsOwner("""
            INSERT INTO iam.cfg_role_function_assignments (tenant_id, code, version, effective_from, record_status, created_by, change_reason, role_code, function_code)
            SELECT @t, @role || ':' || f, 1, current_date - 1, 'EFFECTIVE', 'seed', 'test seed', @role, f FROM unnest(@functions::text[]) AS f
            """, ("t", tenantId), ("role", role), ("functions", functions));
        return role;
    }

    /// <summary>Creates the user (or reuses it) and assigns a new role granting the functions (ADR-026). Returns the user ID.</summary>
    public static async Task<Guid> UserWithFunctions(this TestDatabase database, Guid tenantId, string subject, params string[] functions)
    {
        var userId = await database.UserWithPermissions(tenantId, subject);
        await database.AssignRole(tenantId, userId, await database.RoleWithFunctions(tenantId, functions));
        return userId;
    }

    /// <summary>
    /// Grants a role (seeded by the caller under its own code) the functions that need only these permissions, from the
    /// day given (ADR-026 §7), as the release converts a permission set.
    /// </summary>
    public static Task GrantFunctionsWithin(this TestDatabase database, Guid tenantId, string roleCode, DateOnly from, params string[] permissions) =>
        database.ExecuteAsOwner("""
            INSERT INTO iam.cfg_role_function_assignments (tenant_id, code, version, effective_from, record_status, created_by, change_reason, role_code, function_code)
            SELECT @t, @role || ':' || f, 1, @from, 'EFFECTIVE', 'seed', 'test seed', @role, f FROM iam.functions_within(@permissions) AS f
            ON CONFLICT DO NOTHING
            """, ("t", tenantId), ("role", roleCode), ("from", from), ("permissions", permissions));

    /// <summary>Converts roles, SoD rules and channel rows seeded with permissions to functions, as the release does (ADR-026 §7).</summary>
    public static Task AdoptFunctionCatalogue(this TestDatabase database) => database.ExecuteAsOwner("SELECT iam.adopt_function_catalogue()");

    public static Task AssignRole(this TestDatabase database, Guid tenantId, Guid userId, string role) =>
        database.ExecuteAsOwner("""
            INSERT INTO iam.user_role_assignment (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by,
                                                  user_id, role_code, valid_from, assigned_by, status)
            VALUES (@t, gen_random_uuid(), 1, now(), 'seed', now(), 'seed', @u, @role, current_date - 1, @u, 'ACTIVE')
            """, ("t", tenantId), ("u", userId), ("role", role));
}
