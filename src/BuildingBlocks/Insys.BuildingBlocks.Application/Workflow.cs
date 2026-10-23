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

namespace Insys.BuildingBlocks.Application;

/// <summary>
/// The owning domain's business validation for a workflow transition (WFL-002). A workflow definition names it in
/// domain_validation (for example "general.endorsement.validate"); the module that owns the business object registers
/// an implementation, and the Workflow module calls it before every transition. A definition that names a validator
/// nobody registered is refused, never skipped.
/// </summary>
public interface IWorkflowTransitionValidator
{
    /// <summary>The service code the definitions use.</summary>
    string ServiceCode { get; }

    Task<Result> Validate(string businessRefType, Guid businessRefId, string fromState, string toState, IReadOnlyDictionary<string, object?> variables,
        CancellationToken cancellationToken);
}
