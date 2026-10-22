using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.Identity.Contracts;
using Insys.Identity.Domain.Users;
using Insys.Identity.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.Identity.Infrastructure;

/// <summary>The roles an active user holds today, read on Identity's own connection and role (ADR-001).</summary>
internal sealed class UserRoleQuery([FromKeyedServices(IdentityDbContext.SchemaName)] NpgsqlDataSource dataSource, IClock clock) : IUserRoleQuery
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
        command.Parameters.AddWithValue("today", clock.UtcToday);
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
}
