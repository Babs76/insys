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

/// <summary>Platform-level settings.</summary>
public sealed class PlatformOptions
{
    /// <summary>
    /// The tenant of the platform operator. Its users administer the platform: they register tenants (TEN-008) and
    /// maintain global static data (scopes G and GT). Requests from any other tenant can never do either.
    /// </summary>
    public Guid OperatorTenantId { get; set; }
}

/// <summary>
/// A command or query a platform operator issues on behalf of a tenant, such as suspending it. Only module contexts that
/// allow it (the Tenancy module) run such a request's transaction as the target tenant (TEN-007).
/// </summary>
public interface ITargetTenantRequest
{
    Guid TargetTenantId { get; }
}
