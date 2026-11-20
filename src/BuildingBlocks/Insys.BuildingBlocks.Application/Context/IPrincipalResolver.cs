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
    /// <summary>
    /// SRS-002 (OQ-197): the functions refused because the user's roles give both sides of a BLOCK segregation-of-duties rule in force, by function. Each is
    /// left out of <see cref="Usable"/>, and its refusal is the block's code.
    /// </summary>
    public IReadOnlyDictionary<string, SegregationBlock>? Blocked { get; init; }

    /// <summary>The permissions only a blocked function gave the user, by permission: a check on the permission alone (an approval's) names the block.</summary>
    public IReadOnlyDictionary<string, SegregationBlock>? BlockedPermissions { get; init; }

    /// <summary>Null when the user may use any of the codes (an operation shared by two portals has one per portal); else the first one's refusal.</summary>
    public string? Refusal(IReadOnlyList<string> codes) =>
        codes.Any(Usable.Contains) ? null : codes.Count == 0 ? null : Refusals.GetValueOrDefault(codes[0], Domain.ErrorCodes.FunctionDisabled);
}

/// <summary>
/// A function refused for segregation of duties (SRS-002, OQ-197): the user holds both it and <paramref name="HeldWith"/>, the other side of
/// <paramref name="Rule"/>. <paramref name="Code"/> is the refusal's catalogued error code (Identity's).
/// </summary>
public sealed record SegregationBlock(string Function, string HeldWith, string Rule, string Code)
{
    /// <summary>The audit operation of a refusal (IAM-006), event type ACCESS, target the function refused.</summary>
    public const string AuditOperation = "SegregationOfDutiesRefused";
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
    /// As <see cref="Resolve"/>, for a job acting for a user (AIA-017): the same checks, but it isn't a sign-in, so it records none (no last sign-in, and an
    /// invited user isn't activated: only an active one is acted for).
    /// </summary>
    Task<ResolvedPrincipal?> ResolveForJob(Guid tenantId, string subject, CancellationToken cancellationToken) => Resolve(tenantId, subject, cancellationToken);

    /// <summary>
    /// For a token whose subject is unknown: whether it is a user of the tenant still waiting for its identity to be linked
    /// (BR-018), found by the identity provider's username. The caller is then refused with IDENTITY_NOT_LINKED, a retryable
    /// refusal, rather than FORBIDDEN.
    /// </summary>
    Task<bool> AwaitsIdentity(Guid tenantId, string? username, CancellationToken cancellationToken) => Task.FromResult(false);
}
