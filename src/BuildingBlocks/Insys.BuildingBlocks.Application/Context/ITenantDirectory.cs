namespace Insys.BuildingBlocks.Application.Context;

/// <summary>Lists tenants for background work that runs per tenant, such as the outbox relay.</summary>
public interface ITenantDirectory
{
    Task<IReadOnlyList<Guid>> GetActiveTenantIds(CancellationToken cancellationToken);
}
