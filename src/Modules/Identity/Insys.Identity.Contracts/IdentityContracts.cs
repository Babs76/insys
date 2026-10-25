/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Identity
 * CONFIDENTIALITY AND INTELLECTUAL PROPERTY NOTICE
 * This source code and its associated documentation contain confidential and
 * proprietary information belonging to OpenFactor Technology Group or its
 * applicable intellectual property rights holder.
 * This material is provided solely for authorised development, maintenance,
 * operation or evaluation purposes, as applicable. No person may reproduce,
 * modify, distribute, disclose, publish or otherwise use this material except
 * as expressly permitted by an applicable written agreement or licence.
 * No ownership rights or licence to any intellectual property are granted
 * by the inclusion of this notice, except as expressly provided in an
 * applicable written agreement or licence.
 * Third-party software, open-source components and customer-owned materials
 * remain subject to their respective ownership rights and applicable licences
 * or agreements.
 * UNAUTHORISED ACCESS, USE, COPYING OR DISCLOSURE IS PROHIBITED.
 * ============================================================================
 */
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

/// <summary>An unlocked user may sign in again; its Keycloak user is enabled again (keycloak-user-provisioning §5).</summary>
[IntegrationEvent("iam.UserReenabled")]
public sealed record UserReenabled(Guid UserId) : IIntegrationEvent;

/// <summary>A user of INSYS's own realm waits for its Keycloak user (keycloak-user-provisioning §4). Carries no personal data (INT-007).</summary>
[IntegrationEvent("iam.UserIdentityRequested")]
public sealed record UserIdentityRequested(Guid UserId) : IIntegrationEvent;

/// <summary>Keycloak's required-actions email is to be sent to the user (UPDATE_PASSWORD, CONFIGURE_TOTP).</summary>
[IntegrationEvent("iam.UserInvitationRequested")]
public sealed record UserInvitationRequested(Guid UserId, IReadOnlyList<string> Actions) : IIntegrationEvent;

/// <summary>Public query interface: the roles a user holds today, for approval authority (WFL-004). Read on Identity's own connection.</summary>
public interface IUserRoleQuery
{
    Task<IReadOnlyList<string>> EffectiveRoles(Guid tenantId, Guid userId, CancellationToken cancellationToken);
}

/// <summary>A privileged role assignment is approved in Workflow (SRS-002, ADR-009): one approval per assignment.</summary>
public static class IdentityApprovals
{
    public const string BusinessRefType = "ROLE_ASSIGNMENT";

    public const string ApprovalType = "PRIVILEGED_ROLE";

    /// <summary>A new tenant's first administrator (OQ-067), approved by a second platform administrator.</summary>
    public const string TenantAdministratorBusinessRefType = "TENANT_ADMIN_REQUEST";

    public const string TenantAdministratorApprovalType = "TENANT_ADMINISTRATOR";

    /// <summary>An external user's link to a party, intermediary or provider (external-user-access.md §1), approved by a second person.</summary>
    public const string SubjectLinkBusinessRefType = "SUBJECT_LINK";

    public const string SubjectLinkApprovalType = "EXTERNAL_SUBJECT_LINK";
}

/// <summary>An external user's link took effect or ended: Identity drops cached principals for the tenant (IAM-005).</summary>
[IntegrationEvent("iam.SubjectLinkChanged")]
public sealed record SubjectLinkChanged(Guid UserId) : IIntegrationEvent;

/// <summary>
/// A new tenant's first administrator exists (OQ-067). Published in the platform operator tenant; Tenancy completes the
/// tenant's ORGANISATION_AND_ACCESS onboarding step when it is the current one.
/// </summary>
[IntegrationEvent("iam.TenantAdministratorProvisioned")]
public sealed record TenantAdministratorProvisioned(Guid TargetTenantId, Guid UserId, string RoleCode) : IIntegrationEvent;

/// <summary>
/// The tenant's staff roles in force with their permissions (onboarding readiness CHANNELS): every role except those whose
/// every holder is an external (portal) user, who act through partner channels instead.
/// </summary>
public interface IStaffRoles
{
    /// <summary>The staff roles in force with the functions they grant (ADR-026).</summary>
    Task<IReadOnlyList<(string RoleCode, IReadOnlyList<string> Functions)>> InForce(Guid tenantId, DateOnly day, CancellationToken cancellationToken);
}
