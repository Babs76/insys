/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : General
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
using Insys.BuildingBlocks.Application;

namespace Insys.General.Infrastructure;

/// <summary>BR-114: deciding a non-renewal also needs general_policy.non_renew, so a delegation row alone leaves no one able to decide one.</summary>
internal sealed class NonRenewalApprovalConditions : IApprovalTypeConditions
{
    public Task<IReadOnlyList<ApprovalTypeCondition>> For(Guid tenantId, DateOnly day, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ApprovalTypeCondition>>([new(Contracts.GeneralApprovals.NonRenewalApprovalType, true, Contracts.GeneralPermissions.PolicyNonRenew)]);
}
