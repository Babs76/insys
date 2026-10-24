/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : ClaimsFramework
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
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.ClaimsFramework.Infrastructure.Persistence;

/// <summary>
/// What a claim payment instruction covers (F103), for the payment and payout batch item that pay it: its registered
/// claim's product and node, as the instruction itself is scoped.
/// </summary>
internal sealed class ClaimsRecordScopes([FromKeyedServices(ClaimsFrameworkDbContext.SchemaName)] NpgsqlDataSource dataSource) : IBusinessRecordScopes
{
    public bool Covers(string businessRefType) => businessRefType == Contracts.ClaimsApprovals.InstructionBusinessRefType;

    public async Task<IReadOnlyList<(string? ProductCode, string? OrgNodeCode)>?> ScopesOf(Guid tenantId, string businessRefType, Guid businessRefId,
        CancellationToken cancellationToken) =>
        Covers(businessRefType)
            ? (await RecordScopeReader.ReadAll(dataSource, tenantId, """
                SELECT r.product_code, r.org_node_code FROM claims.claim_payment_instruction i
                JOIN claims.claim_register r ON r.tenant_id = i.tenant_id AND r.claim_number = i.claim_number WHERE i.id = @id
                """, businessRefId, cancellationToken)).Select(s => (s.Product, s.OrgNode)).ToList()
            : null;
}
