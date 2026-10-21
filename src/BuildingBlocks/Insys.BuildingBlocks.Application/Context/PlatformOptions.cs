namespace Insys.BuildingBlocks.Application.Context;

/// <summary>Platform-level settings.</summary>
public sealed class PlatformOptions
{
    /// <summary>
    /// The tenant of the platform operator. Its users administer the platform: they register tenants (TEN-008) and
    /// maintain global static data (scopes G and GT). Requests from any other tenant can never do either.
    /// </summary>
    public Guid OperatorTenantId { get; set; }
}

/// <summary>
/// A command or query a platform operator issues on behalf of a tenant, such as suspending it. Only module contexts that
/// allow it (the Tenancy module) run such a request's transaction as the target tenant (TEN-007).
/// </summary>
public interface ITargetTenantRequest
{
    Guid TargetTenantId { get; }
}
