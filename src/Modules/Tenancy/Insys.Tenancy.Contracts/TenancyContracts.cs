/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Tenancy
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

namespace Insys.Tenancy.Contracts;

/// <summary>Permission codes for tenant administration; platform operator tenant only.</summary>
public static class TenancyPermissions
{
    public const string Read = "tenant.read";
    public const string Create = "tenant.create";
    public const string Update = "tenant.update";
}

/// <summary>Consumed by Identity: block sign-in for the tenant (domain-model.json, iam consumes TenantSuspended).</summary>
/// <summary>The tenant became Active: its users' cached access (configuration only while Onboarding) is dropped.</summary>
[IntegrationEvent("tenant.TenantActivated")]
public sealed record TenantActivated(Guid TenantId) : IIntegrationEvent;

[IntegrationEvent("tenant.TenantSuspended")]
public sealed record TenantSuspended(Guid TenantId) : IIntegrationEvent;

/// <summary>Public query interface: whether a tenant may be used. Read on the Tenancy module's own connection.</summary>
public interface ITenantStatusQuery
{
    Task<bool> IsActive(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>
    /// What the tenant's users may do (decided in Wave 0b batch 6): everything when Active; identity, static data and
    /// configuration while Onboarding, so they load its configuration (business commands are refused with
    /// TENANT_NOT_ACTIVE); nothing when Suspended or Terminated. The background jobs run for Active tenants only.
    /// </summary>
    Task<TenantUserAccess> UserAccess(Guid tenantId, CancellationToken cancellationToken);
}

public enum TenantUserAccess
{
    None,
    ConfigurationOnly,
    Full,
}

/// <summary>What a tenant allows of the function catalogue on a day (ADR-026 §2): the functions it disabled and the features it is entitled to.</summary>
public sealed record TenantFunctionSettings(IReadOnlySet<string> DisabledFunctions, IReadOnlySet<string> EntitledFeatures);

/// <summary>
/// Public query interface: tenant.cfg_tenant_function_availability (a function with no row in force is enabled) and
/// tenant.cfg_feature_entitlements (global rows naming the tenant by its code), in force on the day.
/// </summary>
public interface ITenantFunctionSettings
{
    Task<TenantFunctionSettings> For(Guid tenantId, DateOnly day, CancellationToken cancellationToken);
}

/// <summary>Public query interface: the tenant's base currency on a day (tenant.cfg_locale_defaults), or null when none is in force.</summary>
public interface ITenantBaseCurrency
{
    Task<string?> BaseCurrency(Guid tenantId, DateOnly day, CancellationToken cancellationToken);
}

/// <summary>The tenant's locale defaults that formatting needs (BR-022): its ISO 3166 country and ISO 4217 base currency.</summary>
public sealed record TenantFormatting(string Country, string BaseCurrency);

/// <summary>Public query interface: the tenant's locale on a day (tenant.cfg_locale_defaults), or null when none is in force.</summary>
public interface ITenantFormatting
{
    Task<TenantFormatting?> Formatting(Guid tenantId, DateOnly day, CancellationToken cancellationToken);
}

/// <summary>Public query interface: the tenant's IANA time zone on a day (tenant.cfg_locale_defaults), or null when none is in force.</summary>
public interface ITenantTimeZone
{
    Task<string?> TimeZone(Guid tenantId, DateOnly day, CancellationToken cancellationToken);
}

/// <summary>A tenant as other modules show it: its code, legal name and status (Onboarding, Active, Suspended or Terminated).</summary>
public sealed record TenantProfile(Guid Id, string TenantCode, string LegalName, string Status);

/// <summary>Public query interface: a tenant's profile, read on the Tenancy module's own connection; null for an unknown tenant.</summary>
public interface ITenantProfiles
{
    Task<TenantProfile?> Get(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>A node of a tenant's organisation structure (tenant.mst_organisation_structure): a legal entity, region or branch.</summary>
public sealed record OrganisationNode(string NodeCode, string Type, string? Parent);

/// <summary>Public query interface: a tenant's organisation structure in force on a day (F103, addenda/access-scopes.md §3).</summary>
public interface IOrganisationStructure
{
    Task<IReadOnlyList<OrganisationNode>> Nodes(Guid tenantId, DateOnly day, CancellationToken cancellationToken);
}

public static class OrganisationNodes
{
    /// <summary>The nodes and every node below them (parent links): a scope on a node covers its whole subtree (§3).</summary>
    public static IReadOnlySet<string> WithDescendants(IReadOnlyList<OrganisationNode> nodes, IEnumerable<string> codes)
    {
        var children = nodes.Where(n => n.Parent is not null).ToLookup(n => n.Parent!, n => n.NodeCode, StringComparer.Ordinal);
        var result = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(codes);
        while (pending.TryPop(out var code))
        {
            if (result.Add(code))
            {
                foreach (var child in children[code])
                {
                    pending.Push(child);
                }
            }
        }

        return result;
    }

    /// <summary>The root legal entity: a node with no parent (the first by code when there are several), or null without a structure.</summary>
    public static string? Root(IReadOnlyList<OrganisationNode> nodes) =>
        nodes.Where(n => n.Parent is null).OrderBy(n => n.NodeCode, StringComparer.Ordinal).Select(n => n.NodeCode).FirstOrDefault();
}
