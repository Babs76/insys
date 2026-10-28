/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Contracts
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
namespace Insys.BuildingBlocks.Contracts;

/// <summary>
/// Public query interface (BR-030): the records of a module that reference a product version, so Product Factory refuses
/// to withdraw a Published version not yet Effective while any quote, proposal or policy references it, naming each.
/// Each module that sells products registers one.
/// </summary>
public interface IProductVersionReferences
{
    /// <summary>The referencing records in the tenant, each as "&lt;module&gt; &lt;record&gt; &lt;number&gt;", for example "GENERAL quote Q-000123".</summary>
    Task<IReadOnlyList<string>> Of(Guid tenantId, string productCode, int version, CancellationToken cancellationToken);
}
