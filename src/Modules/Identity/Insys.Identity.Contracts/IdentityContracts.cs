using Insys.BuildingBlocks.Contracts;

namespace Insys.Identity.Contracts;

/// <summary>Permission codes for user administration (iam.ref_permission_catalogue format).</summary>
public static class IdentityPermissions
{
    public const string UserRead = "user.read";
    public const string UserCreate = "user.create";
    public const string UserUpdate = "user.update";
    public const string PrivilegedAccessRead = "privileged_access.read";
    public const string PrivilegedAccessCreate = "privileged_access.create";
    public const string PrivilegedAccessApprove = "privileged_access.approve";
    public const string AccessReviewRead = "access_review.read";
    public const string AccessReviewCreate = "access_review.create";
    public const string AccessReviewUpdate = "access_review.update";
}

/// <summary>A revoked review item, by reference only (INT-007).</summary>
public sealed record RevokedAccess(Guid UserId, string RoleCode);

/// <summary>Consumed by Identity itself: the roles revoked in the review end today (IAM-005).</summary>
[IntegrationEvent("iam.AccessReviewCompleted")]
public sealed record AccessReviewCompleted(Guid ReviewId, IReadOnlyList<RevokedAccess> Revoked) : IIntegrationEvent;

/// <summary>Consumed by Identity itself to drop cached permissions for the tenant (IAM-005).</summary>
[IntegrationEvent("iam.RoleAssigned")]
public sealed record RoleAssigned(Guid UserId, string RoleCode, DateOnly ValidFrom, DateOnly? ValidTo) : IIntegrationEvent;

[IntegrationEvent("iam.RoleRevoked")]
public sealed record RoleRevoked(Guid UserId, string RoleCode) : IIntegrationEvent;

[IntegrationEvent("iam.UserDisabled")]
public sealed record UserDisabled(Guid UserId) : IIntegrationEvent;

/// <summary>Public query interface: the roles a user holds today, for approval authority (WFL-004). Read on Identity's own connection.</summary>
public interface IUserRoleQuery
{
    Task<IReadOnlyList<string>> EffectiveRoles(Guid tenantId, Guid userId, CancellationToken cancellationToken);
}
