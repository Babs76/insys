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
    IReadOnlyCollection<string>? RoleCodes = null, ExternalSubject? External = null, bool ConfigurationOnly = false, FunctionAccess? Functions = null);

/// <summary>
/// The functions a user may use now (ADR-026 §2), and why each other function of the catalogue is refused, in the
/// order of checks: the tenant still Onboarding (TENANT_NOT_ACTIVE), disabled (FUNCTION_DISABLED), not entitled
/// (FEATURE_NOT_ENTITLED), then not granted (FUNCTION_NOT_GRANTED). A code the catalogue does not have in force is
/// not available on the platform (FUNCTION_DISABLED).
/// </summary>
/// <param name="Usable">The functions the user's roles in effect grant and the tenant allows now.</param>
/// <param name="Refusals">For every function in force in the catalogue, the tenant's refusal; FUNCTION_NOT_GRANTED for one it allows.</param>
public sealed record FunctionAccess(IReadOnlySet<string> Usable, IReadOnlyDictionary<string, string> Refusals)
{
    /// <summary>Null when the user may use any of the codes (an operation shared by two portals has one per portal); else the first one's refusal.</summary>
    public string? Refusal(IReadOnlyList<string> codes) =>
        codes.Any(Usable.Contains) ? null : codes.Count == 0 ? null : Refusals.GetValueOrDefault(codes[0], Domain.ErrorCodes.FunctionDisabled);
}

/// <summary>
/// Resolves a token's subject to a user and permissions, per request, from IAM role assignments. Access tokens carry
/// only sub and tenant_id, so a revoked role stops working on the next request (IAM-005).
/// </summary>
public interface IPrincipalResolver
{
    /// <summary>Returns null when the subject is unknown, locked or disabled in the tenant, or the tenant is neither active nor onboarding.</summary>
    Task<ResolvedPrincipal?> Resolve(Guid tenantId, string subject, CancellationToken cancellationToken);

    /// <summary>
    /// For a token whose subject is unknown: whether it is a user of the tenant still waiting for its identity to be linked
    /// (BR-018), found by the identity provider's username. The caller is then refused with IDENTITY_NOT_LINKED, a retryable
    /// refusal, rather than FORBIDDEN.
    /// </summary>
    Task<bool> AwaitsIdentity(Guid tenantId, string? username, CancellationToken cancellationToken) => Task.FromResult(false);
}
