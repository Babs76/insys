using Insys.BuildingBlocks.Contracts;

namespace Insys.Identity.Contracts;

/// <summary>Permission codes for user administration (iam.ref_permission_catalogue format).</summary>
public static class IdentityPermissions
{
    public const string UserRead = "user.read";
    public const string UserCreate = "user.create";
    public const string UserUpdate = "user.update";
}

/// <summary>Consumed by Identity itself to drop cached permissions for the tenant (IAM-005).</summary>
[IntegrationEvent("iam.RoleAssigned")]
public sealed record RoleAssigned(Guid UserId, string RoleCode, DateOnly ValidFrom, DateOnly? ValidTo) : IIntegrationEvent;

[IntegrationEvent("iam.RoleRevoked")]
public sealed record RoleRevoked(Guid UserId, string RoleCode) : IIntegrationEvent;

[IntegrationEvent("iam.UserDisabled")]
public sealed record UserDisabled(Guid UserId) : IIntegrationEvent;
