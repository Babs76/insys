using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.Distribution.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.General.Infrastructure.Persistence;

/// <summary>Who sold a General policy, for commission (OQ-062 item 2), on General's own connection.</summary>
internal sealed class GeneralPolicyDistribution([FromKeyedServices(GeneralDbContext.SchemaName)] NpgsqlDataSource dataSource) : IPolicyDistribution
{
    public string SourceDomain => "GENERAL";

    public async Task<PolicyDistribution?> For(Guid tenantId, Guid policyRef, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("SELECT product_code, intermediary_code FROM general.policy WHERE tenant_id = @t AND id = @p", connection, transaction);
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
