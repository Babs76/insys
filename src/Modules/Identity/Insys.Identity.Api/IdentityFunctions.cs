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
using Insys.BuildingBlocks.Api.Functions;

namespace Insys.Identity.Api;

/// <summary>
/// The Identity module's functions (ADR-026): each screen with the read operations it covers, each action with its
/// operation, by endpoint name, in the sub-domains of docs/specs/INSYS_NAVIGATION.md. Portal screens are declared once
/// per portal. The function catalogue is generated from these.
/// </summary>
internal static class IdentityFunctions
{
    public static readonly IReadOnlyList<FunctionDeclaration> All =
    [
        .. new FunctionMap(Audiences.StaffAndPlatform, null)
        .Screen("IDENTITY_ACCESS_REVIEWS", "IDENTITY_ACCESS", "Access reviews", "identity/access-reviews", "ListAccessReviews", "GetAccessReview")
        .Action("START_REVIEW", "IDENTITY_ACCESS_REVIEWS", "StartReview")
        .Action("CONFIRM_ACCESS", "IDENTITY_ACCESS_REVIEWS", "ConfirmAccess")
        .Action("REVOKE_ACCESS", "IDENTITY_ACCESS_REVIEWS", "RevokeAccess")
        .Action("COMPLETE_REVIEW", "IDENTITY_ACCESS_REVIEWS", "CompleteReview")
        .Screen("IDENTITY_PRIVILEGED_ACCESS", "IDENTITY_ACCESS", "Privileged access", "identity/privileged-access", "ListPrivilegedAccess", "GetPrivilegedAccess")
        .Action("REQUEST_ACCESS", "IDENTITY_PRIVILEGED_ACCESS", "RequestAccess")
        .Action("APPROVE_ACCESS", "IDENTITY_PRIVILEGED_ACCESS", "ApproveAccess")
        .Action("REJECT_ACCESS", "IDENTITY_PRIVILEGED_ACCESS", "RejectAccess")
        .Screen("IDENTITY_ROLES", "IDENTITY_ACCESS", "Roles", "identity/roles", "ListRoles")
        .Screen("IDENTITY_TENANT_ADMINISTRATORS", "TENANTS", "First administrator", "identity/tenant-administrators", "ListTenantAdministratorRequests", "GetTenantAdministratorRequest")
        .Action("PROVISION_TENANT_ADMINISTRATOR", "IDENTITY_TENANT_ADMINISTRATORS", "ProvisionTenantAdministrator")
        .Screen("IDENTITY_USERS", "IDENTITY_ACCESS", "Users", "identity/users", "ListUsers", "GetUser", "IdentityGetSubjectLink")
        .Action("PROVISION_USER", "IDENTITY_USERS", "ProvisionUser")
        .Action("RESEND_INVITATION", "IDENTITY_USERS", "ResendInvitation")
        .Action("IDENTITY_LINK_SUBJECT", "IDENTITY_USERS", "IdentityLinkSubject")
        .Action("IDENTITY_END_SUBJECT_LINK", "IDENTITY_USERS", "IdentityEndSubjectLink")
        .Action("ASSIGN_ROLE", "IDENTITY_USERS", "AssignRole")
        .Action("REVOKE_ROLE", "IDENTITY_USERS", "RevokeRole")
        .Action("LOCK_USER", "IDENTITY_USERS", "LockUser")
        .Action("UNLOCK_USER", "IDENTITY_USERS", "UnlockUser")
        .Action("DISABLE_USER", "IDENTITY_USERS", "DisableUser")
        .Screen("IDENTITY_ROLE_ASSIGNMENTS", "IDENTITY_ACCESS", "Roles", "identity/role-assignments", "IdentityListScopedAssignments")
            .Functions,
    ];
}
