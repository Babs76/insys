/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Application
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
namespace Insys.BuildingBlocks.Application.Context;

/// <summary>The caller as the Identity module knows it.</summary>
/// <param name="UserId">The iam.user_account ID, used as the actor in audit and maker-checker.</param>
/// <param name="Permissions">Permission codes from the user's current role assignments.</param>
/// <param name="Grants">Per permission, the reach of each assignment giving it (F103); a permission missing here is unrestricted.</param>
/// <param name="RoleCodes">The roles in effect, for channel permission rows (F005).</param>
/// <param name="External">The party, intermediary or provider an external user is, from their approved link; null for staff.</param>
public sealed record ResolvedPrincipal(Guid UserId, IReadOnlySet<string> Permissions, IReadOnlyDictionary<string, IReadOnlyList<PermissionGrant>>? Grants = null,
    IReadOnlyCollection<string>? RoleCodes = null, ExternalSubject? External = null);

/// <summary>
/// Resolves a token's subject to a user and permissions, per request, from IAM role assignments. Access tokens carry
/// only sub and tenant_id, so a revoked role stops working on the next request (IAM-005).
/// </summary>
public interface IPrincipalResolver
{
    /// <summary>Returns null when the subject is unknown, locked or disabled in the tenant, or the tenant is neither active nor onboarding.</summary>
    Task<ResolvedPrincipal?> Resolve(Guid tenantId, string subject, CancellationToken cancellationToken);
}
