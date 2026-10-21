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

    /// <summary>A role version in force today with the given permissions. Returns the role code.</summary>
    public static async Task<string> RoleWithPermissions(this TestDatabase database, Guid tenantId, params string[] permissions)
    {
        var role = "R_" + Guid.NewGuid().ToString("N")[..20].ToUpperInvariant();
        await database.ExecuteAsOwner("""
            INSERT INTO iam.cfg_roles_and_permission_sets (tenant_id, code, version, effective_from, record_status, created_by, change_reason, role_code, permissions)
            VALUES (@t, @role, 1, current_date - 1, 'EFFECTIVE', 'seed', 'test seed', @role, @permissions)
            """, ("t", tenantId), ("role", role), ("permissions", permissions));
        return role;
    }

    public static Task AssignRole(this TestDatabase database, Guid tenantId, Guid userId, string role) =>
        database.ExecuteAsOwner("""
            INSERT INTO iam.user_role_assignment (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by,
                                                  user_id, role_code, valid_from, assigned_by)
            VALUES (@t, gen_random_uuid(), 1, now(), 'seed', now(), 'seed', @u, @role, current_date - 1, @u)
            """, ("t", tenantId), ("u", userId), ("role", role));
}
