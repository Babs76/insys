/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Health
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
using Insys.BuildingBlocks.Application.Messaging;
using Insys.Workflow.Contracts;

namespace Insys.Health.Application;

/// <summary>BR-101: a portal user may link one of its Health (medical) claims to their case exactly when their portal read of it shows it.</summary>
public sealed class PortalHealthClaimCaseLinks(IDispatcher dispatcher) : IPortalCaseLinks
{
    public bool Covers(string linkType) => linkType == CaseLinkTypes.Claim;

    public async Task<bool> Visible(string linkType, Guid recordId, CancellationToken cancellationToken) =>
        Covers(linkType) && (await dispatcher.Query(new GetPortalMedicalClaim(recordId), cancellationToken)).IsSuccess;
}
