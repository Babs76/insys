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

/// <summary>
/// Public query interface: whether a tenant is a sample tenant, such as OFTZA (Workstream F). Its notifications and payments go only to stubs, even where a real provider is connected.
/// The mark is set when the tenant is created and never changes, so a real customer always gets a new tenant.
/// </summary>
public interface ISampleTenants
{
    /// <summary>An unknown tenant counts as a sample one: this guards real money and real people, so it fails closed.</summary>
    Task<bool> IsSample(Guid tenantId, CancellationToken cancellationToken);
}
