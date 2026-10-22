using Insys.BuildingBlocks.Contracts;

namespace Insys.Tenancy.Contracts;

/// <summary>Permission codes for tenant administration; platform operator tenant only.</summary>
public static class TenancyPermissions
{
    public const string Read = "tenant.read";
    public const string Create = "tenant.create";
    public const string Update = "tenant.update";
}

/// <summary>Consumed by Identity: block sign-in for the tenant (domain-model.json, iam consumes TenantSuspended).</summary>
[IntegrationEvent("tenant.TenantSuspended")]
public sealed record TenantSuspended(Guid TenantId) : IIntegrationEvent;

/// <summary>Public query interface: whether a tenant may be used (Active). Read on the Tenancy module's own connection.</summary>
public interface ITenantStatusQuery
{
    Task<bool> IsActive(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>Public query interface: the tenant's base currency on a day (tenant.cfg_locale_defaults), or null when none is in force.</summary>
public interface ITenantBaseCurrency
{
    Task<string?> BaseCurrency(Guid tenantId, DateOnly day, CancellationToken cancellationToken);
}
