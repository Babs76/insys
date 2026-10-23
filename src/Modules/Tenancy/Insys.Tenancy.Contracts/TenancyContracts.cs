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
[IntegrationEvent("tenant.TenantSuspended")]
public sealed record TenantSuspended(Guid TenantId) : IIntegrationEvent;

/// <summary>Public query interface: whether a tenant may be used (Active). Read on the Tenancy module's own connection.</summary>
public interface ITenantStatusQuery
{
    Task<bool> IsActive(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>Public query interface: the tenant's base currency on a day (tenant.cfg_locale_defaults), or null when none is in force.</summary>
public interface ITenantBaseCurrency
{
    Task<string?> BaseCurrency(Guid tenantId, DateOnly day, CancellationToken cancellationToken);
}

/// <summary>A tenant as other modules show it: its code, legal name and status (Onboarding, Active, Suspended or Terminated).</summary>
public sealed record TenantProfile(Guid Id, string TenantCode, string LegalName, string Status);

/// <summary>Public query interface: a tenant's profile, read on the Tenancy module's own connection; null for an unknown tenant.</summary>
public interface ITenantProfiles
{
    Task<TenantProfile?> Get(Guid tenantId, CancellationToken cancellationToken);
}
