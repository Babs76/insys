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
/// A module whose records are scoped (F103) says which products and organisation nodes a record of one of its business
/// reference types covers, so another module holding something about that record (a payment against an invoice, say) shows
/// it only to a caller whose scope covers all of them (OQ-081: no figures of another branch or product through a shared
/// account). Each pair is (product, node); either may be null.
/// </summary>
public interface IBusinessRecordScopes
{
    bool Covers(string businessRefType);

    /// <summary>The record's products and nodes, or null when it is not found.</summary>
    Task<IReadOnlyList<(string? ProductCode, string? OrgNodeCode)>?> ScopesOf(Guid tenantId, string businessRefType, Guid businessRefId, CancellationToken cancellationToken);
}
