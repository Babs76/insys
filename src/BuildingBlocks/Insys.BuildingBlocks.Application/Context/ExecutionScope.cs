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

public enum ActorType
{
    User,
    Service,
}

/// <summary>The tenant for the current unit of work. Comes only from the authenticated principal or a service identity (TEN-002).</summary>
public interface ITenantContext
{
    Guid? TenantId { get; }

    bool HasTenant => TenantId.HasValue;

    /// <summary>Returns the tenant or throws <see cref="TenantContextMissingException"/>; never falls back to a default.</summary>
    Guid RequireTenantId() => TenantId ?? throw new TenantContextMissingException();
}

/// <summary>Who is acting and why: used for audit, created_by/updated_by and event metadata.</summary>
public interface IExecutionContext
{
    string ActorId { get; }

    ActorType ActorType { get; }

    Guid CorrelationId { get; }

    Guid? CausationId { get; }

    bool HasPermission(string permission);

    /// <summary>The caller's scope for a permission (F103): unrestricted for a service identity or an unscoped assignment.</summary>
    AccessScope ScopeFor(string permission);

    /// <summary>The scope for the permission the current endpoint requires; unrestricted outside an endpoint (a job, a consumer).</summary>
    AccessScope CurrentScope { get; }

    /// <summary>The permission the current endpoint requires, when the request is to a business endpoint.</summary>
    string? RequestPermission { get; }

    /// <summary>Whether the current request only reads (GET): a record outside the scope is then not found, rather than refused.</summary>
    bool RequestIsRead { get; }
}

public sealed class TenantContextMissingException()
    : InvalidOperationException("No tenant is established for this operation.");

/// <summary>
/// The scoped execution context. It is initialised once per scope, from the authenticated principal
/// (HTTP requests) or from a service identity (background work and event consumers), and cannot be changed afterwards.
/// </summary>
public sealed class ExecutionScope : ITenantContext, IExecutionContext
{
    private HashSet<string> _permissions = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, IReadOnlyList<PermissionGrant>>? _grants;

    public bool IsInitialised { get; private set; }

    public Guid? TenantId { get; private set; }

    public string ActorId { get; private set; } = "anonymous";

    public ActorType ActorType { get; private set; } = ActorType.User;

    public Guid CorrelationId { get; private set; } = Guid.CreateVersion7();

    public Guid? CausationId { get; private set; }

    public bool HasPermission(string permission) => _permissions.Contains(permission);

    public string? RequestPermission { get; private set; }

    public bool RequestIsRead { get; private set; }

    public AccessScope ScopeFor(string permission) =>
        ActorType == ActorType.Service ? AccessScope.Unrestricted
        : !_permissions.Contains(permission) ? AccessScope.None
        : _grants is not null && _grants.TryGetValue(permission, out var grants) ? new AccessScope(grants)
        : AccessScope.Unrestricted;

    public AccessScope CurrentScope => RequestPermission is null ? AccessScope.Unrestricted : ScopeFor(RequestPermission);

    /// <summary>Set once per request by the business modules' endpoint filter, from the endpoint's required permission (F103).</summary>
    public void UseRequest(string permission, bool isRead)
    {
        RequestPermission = permission;
        RequestIsRead = isRead;
    }

    public void InitialiseForUser(Guid? tenantId, string actorId, IEnumerable<string> permissions, Guid? correlationId,
        IReadOnlyDictionary<string, IReadOnlyList<PermissionGrant>>? grants = null)
    {
        EnsureNotInitialised();
        _grants = grants;
        TenantId = tenantId;
        ActorId = actorId;
        ActorType = ActorType.User;
        _permissions = new HashSet<string>(permissions, StringComparer.Ordinal);
        CorrelationId = correlationId ?? CorrelationId;
        IsInitialised = true;
    }

    public void InitialiseForService(Guid tenantId, string serviceName, Guid? correlationId = null, Guid? causationId = null)
    {
        EnsureNotInitialised();
        TenantId = tenantId;
        ActorId = serviceName;
        ActorType = ActorType.Service;
        CorrelationId = correlationId ?? CorrelationId;
        CausationId = causationId;
        IsInitialised = true;
    }

    private void EnsureNotInitialised()
    {
        if (IsInitialised)
        {
            throw new InvalidOperationException("The execution scope is already initialised and cannot be changed.");
        }
    }
}
