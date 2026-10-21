namespace Insys.BuildingBlocks.Application.Context;

/// <summary>The caller as the Identity module knows it.</summary>
/// <param name="UserId">The iam.user_account ID, used as the actor in audit and maker-checker.</param>
/// <param name="Permissions">Permission codes from the user's current role assignments.</param>
public sealed record ResolvedPrincipal(Guid UserId, IReadOnlySet<string> Permissions);

/// <summary>
/// Resolves a token's subject to a user and permissions, per request, from IAM role assignments. Access tokens carry
/// only sub and tenant_id, so a revoked role stops working on the next request (IAM-005).
/// </summary>
public interface IPrincipalResolver
{
    /// <summary>Returns null when the subject is unknown, locked or disabled in the tenant, or the tenant is not active.</summary>
    Task<ResolvedPrincipal?> Resolve(Guid tenantId, string subject, CancellationToken cancellationToken);
}
