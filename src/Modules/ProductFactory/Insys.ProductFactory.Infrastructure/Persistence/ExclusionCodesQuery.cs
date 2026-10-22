using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.ProductFactory.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.ProductFactory.Infrastructure.Persistence;

/// <summary>Exclusions in force with their diagnosis and procedure codes (ADR-012), on Product Factory's own connection.</summary>
internal sealed class ExclusionCodesQuery([FromKeyedServices(ProductFactoryDbContext.SchemaName)] NpgsqlDataSource dataSource) : IExclusionCodes
{
    public async Task<IReadOnlyList<ExclusionCodes>> InForce(Guid tenantId, IReadOnlyCollection<string>? codes, DateOnly day, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT DISTINCT ON (code) code, name, coalesce(diagnosis_codes, '{}'), coalesce(procedure_codes, '{}')
            FROM product.cfg_exclusion_library
            WHERE (tenant_id = @t OR tenant_id IS NULL) AND record_status = ANY(@statuses)
              AND effective_from <= @day AND (effective_to IS NULL OR effective_to > @day)
              AND (@codes::text[] IS NULL OR code = ANY(@codes))
              AND (cardinality(diagnosis_codes) > 0 OR cardinality(procedure_codes) > 0)
            ORDER BY code, tenant_id NULLS LAST, version DESC
            """, connection, transaction);
        command.Parameters.AddWithValue("t", tenantId);
        command.Parameters.AddWithValue("statuses", StaticLifecycle.Configuration.InForce);
        command.Parameters.AddWithValue("day", day);
        command.Parameters.Add(new NpgsqlParameter<string[]?>("codes", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) { TypedValue = codes?.ToArray() });
        var exclusions = new List<ExclusionCodes>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                exclusions.Add(new ExclusionCodes(reader.GetString(0), reader.GetString(1), reader.GetFieldValue<string[]>(2), reader.GetFieldValue<string[]>(3)));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return exclusions;
    }
}
