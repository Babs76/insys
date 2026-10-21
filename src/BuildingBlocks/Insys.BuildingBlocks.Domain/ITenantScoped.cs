namespace Insys.BuildingBlocks.Domain;

/// <summary>Marks an entity stored in a tenant-scoped table (DBA-001). The infrastructure sets and filters <see cref="TenantId"/>.</summary>
public interface ITenantScoped
{
    Guid TenantId { get; }
}
