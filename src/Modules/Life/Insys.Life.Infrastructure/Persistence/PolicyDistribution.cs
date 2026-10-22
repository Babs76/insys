using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.Distribution.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.Life.Infrastructure.Persistence;

/// <summary>Who sold a Life policy, for commission (OQ-062 item 2), on Life's own connection.</summary>
internal sealed class LifePolicyDistribution([FromKeyedServices(LifeDbContext.SchemaName)] NpgsqlDataSource dataSource) : IPolicyDistribution
{
    public string SourceDomain => "LIFE";

    public async Task<PolicyDistribution?> For(Guid tenantId, Guid policyRef, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("SELECT product_code, intermediary_code FROM life.policy WHERE tenant_id = @t AND id = @p", connection, transaction);
        command.Parameters.AddWithValue("t", tenantId);
        command.Parameters.AddWithValue("p", policyRef);
        PolicyDistribution? found = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                found = new PolicyDistribution(SourceDomain, policyRef, reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return found;
    }
}
