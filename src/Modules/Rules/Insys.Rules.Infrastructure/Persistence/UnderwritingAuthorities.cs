using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Rules.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.Rules.Infrastructure.Persistence;

/// <summary>Underwriting authority matrices in force (rules.cfg_authority_matrices), on Rules' own connection.</summary>
internal sealed class UnderwritingAuthorities([FromKeyedServices(RulesDbContext.SchemaName)] NpgsqlDataSource dataSource) : IUnderwritingAuthorities
{
    public async Task<IReadOnlyList<UnderwritingAuthority>> For(Guid tenantId, IReadOnlyCollection<string> roleCodes, string productCode, DateOnly day,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT role_code, product_code, max_sum_assured, decision_types FROM rules.cfg_authority_matrices
            WHERE (tenant_id = @t OR tenant_id IS NULL) AND record_status = ANY(@statuses) AND effective_from <= @day AND (effective_to IS NULL OR effective_to > @day)
              AND role_code = ANY(@roles) AND (product_code IS NULL OR product_code = '' OR product_code = @product)
            """, connection, transaction);
        command.Parameters.AddWithValue("t", tenantId);
        command.Parameters.AddWithValue("statuses", StaticLifecycle.Configuration.InForce);
        command.Parameters.AddWithValue("day", day);
        command.Parameters.AddWithValue("roles", roleCodes.ToArray());
        command.Parameters.AddWithValue("product", productCode);
        var rows = new List<UnderwritingAuthority>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new UnderwritingAuthority(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetDecimal(2), reader.GetFieldValue<string[]>(3)));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return rows;
    }
}
