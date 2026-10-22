using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Life.Contracts;
using Insys.Workflow.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.Life.Infrastructure.Persistence;

/// <summary>
/// Life claim approval authority: the claims authority limits in force (life.cfg_claims_authority_limits) for the
/// claim's type, by role (Phase 5 review, item 5). Read on Life's own connection.
/// </summary>
internal sealed class ClaimsAuthority([FromKeyedServices(LifeDbContext.SchemaName)] NpgsqlDataSource dataSource) : IApprovalAuthoritySource
{
    public bool Decides(string approvalType) => approvalType == LifeApprovals.ClaimApprovalType;

    public async Task<IReadOnlyList<ApprovalAuthority>> For(Guid tenantId, string businessRefType, Guid businessRefId, IReadOnlyCollection<string> roleCodes,
        DateOnly day, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT a.role, a.limit_value FROM life.cfg_claims_authority_limits a
            JOIN life.claim c ON c.tenant_id = a.tenant_id AND c.id = @claim AND c.claim_type = a.claim_type
            WHERE a.tenant_id = @t AND a.record_status = ANY(@statuses) AND a.effective_from <= @day AND (a.effective_to IS NULL OR a.effective_to > @day)
              AND a.role = ANY(@roles)
            """, connection, transaction);
        command.Parameters.AddWithValue("t", tenantId);
        command.Parameters.AddWithValue("claim", businessRefId);
        command.Parameters.AddWithValue("statuses", StaticLifecycle.Configuration.InForce);
        command.Parameters.AddWithValue("day", day);
        command.Parameters.AddWithValue("roles", roleCodes.ToArray());
        var rows = new List<ApprovalAuthority>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new ApprovalAuthority(reader.GetString(0), reader.GetDecimal(1)));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return rows;
    }
}
