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
using Insys.BuildingBlocks.Domain;

namespace Insys.BuildingBlocks.Application.Context;

/// <summary>
/// One role assignment's reach for a permission (F103, docs/specs/addenda/access-scopes.md): the products and the
/// organisation nodes (with every node below them) it covers. Null on a dimension means it is not restricted there.
/// </summary>
public sealed record PermissionGrant(IReadOnlySet<string>? Products, IReadOnlySet<string>? OrgNodes)
{
    public static readonly PermissionGrant All = new(null, null);

    /// <summary>A record is covered when its product and its node are both within the grant. A record with no node is outside a node scope.</summary>
    public bool Allows(string? product, string? orgNode) =>
        (Products is null || (product is not null && Products.Contains(product))) && (OrgNodes is null || (orgNode is not null && OrgNodes.Contains(orgNode)));
}

/// <summary>
/// The caller's scope for one permission (addenda/access-scopes.md §4): in scope for a record when at least one of the
/// assignments giving the permission covers both the record's product and its organisation node.
/// </summary>
public sealed class AccessScope(IReadOnlyList<PermissionGrant> grants)
{
    public static readonly AccessScope Unrestricted = new([PermissionGrant.All]);

    public static readonly AccessScope None = new([]);

    public IReadOnlyList<PermissionGrant> Grants { get; } = grants;

    public bool IsUnrestricted => Grants.Any(g => g.Products is null && g.OrgNodes is null);

    public bool Allows(string? product, string? orgNode) => Grants.Any(g => g.Allows(product, orgNode));

    /// <summary>The one organisation node the scope names, when every grant names exactly that one (a new record's default node, §2).</summary>
    public string? SingleOrgNode =>
        Grants.Count > 0 && Grants.All(g => g.OrgNodes is { Count: > 0 }) && Grants.SelectMany(g => g.OrgNodes!).Distinct(StringComparer.Ordinal).ToList() is [var only]
            ? only
            : null;
}

/// <summary>
/// An action on a record outside the caller's scope for the permission (F103). Answered 403 OUT_OF_SCOPE; a read of
/// such a record is answered 404 instead, so that it is not revealed (§4).
/// </summary>
public sealed class OutOfScopeException(string permission)
    : InvalidOperationException($"The record is outside the caller's scope for {permission}.")
{
    public string Permission { get; } = permission;
}

/// <summary>The two checks every scoped module makes (addenda/access-scopes.md §2 and §4), against the scope of the request's permission.</summary>
public static class AccessScopeChecks
{
    /// <summary>
    /// A loaded record, when it is in scope. Outside the scope a read finds nothing (404) and an action is refused
    /// (403 OUT_OF_SCOPE). Jobs and consumers act as the service and are unrestricted.
    /// </summary>
    public static T? InScope<T>(this IExecutionContext execution, T? record, string? product, string? orgNode)
        where T : class
    {
        if (record is null || execution.CurrentScope.Allows(product, orgNode))
        {
            return record;
        }

        return execution.RequestIsRead ? null : throw new OutOfScopeException(execution.RequestPermission ?? "the request");
    }

    /// <summary>
    /// The organisation node a new record of the product is placed at (§2): the one requested, else the caller's one node,
    /// else the root. It must be a node in force, and in the caller's scope with the product. With no organisation structure
    /// (<paramref name="nodeCodes"/> empty) a record has no node.
    /// </summary>
    public static Result<string?> PlaceNew(this IExecutionContext execution, string? product, string? requested, IReadOnlyCollection<string> nodeCodes, string? root)
    {
        var node = nodeCodes.Count == 0 ? null : requested ?? execution.CurrentScope.SingleOrgNode ?? root;
        if (node is not null && !nodeCodes.Contains(node))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, $"{node} is not a node of the organisation structure in force.");
        }

        if (!execution.CurrentScope.Allows(product, node))
        {
            return Error.Forbidden(ErrorCodes.OutOfScope, "The product or organisation node is outside your scope for creating this record.");
        }

        return node;
    }
}
