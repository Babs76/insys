using Insys.BuildingBlocks.Application.Context;
using Npgsql;

namespace Insys.Api.Platform;

/// <summary>
/// Reads tenant IDs from the tenant register (tenant.tenant, which has no row-level security) for per-tenant
/// background work. TODO(phase-1): move behind the Tenancy module's Contracts and filter by tenant status.
/// </summary>
internal sealed class SqlTenantDirectory(NpgsqlDataSource dataSource) : ITenantDirectory
{
    public async Task<IReadOnlyList<Guid>> GetActiveTenantIds(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT id FROM tenant.tenant ORDER BY id");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var ids = new List<Guid>();
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }
}
