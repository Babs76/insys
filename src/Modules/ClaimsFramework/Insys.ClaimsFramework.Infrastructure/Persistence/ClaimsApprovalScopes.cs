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
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.Workflow.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.ClaimsFramework.Infrastructure.Persistence;

/// <summary>The scope of a claim payment instruction's approval (F103): its registered claim's product and node.</summary>
internal sealed class ClaimsApprovalScopes([FromKeyedServices(ClaimsFrameworkDbContext.SchemaName)] NpgsqlDataSource dataSource) : IApprovalScopeSource
{
    public bool Covers(string businessRefType) => Sql(businessRefType) is not null;

    public async Task<RecordScope?> ScopeOf(Guid tenantId, string businessRefType, Guid businessRefId, CancellationToken cancellationToken) =>
        Sql(businessRefType) is { } sql && await RecordScopeReader.Read(dataSource, tenantId, sql, businessRefId, cancellationToken) is { } scope
            ? new RecordScope(scope.Product, scope.OrgNode)
            : null;

    private static string? Sql(string businessRefType) => businessRefType switch
    {
        Contracts.ClaimsApprovals.InstructionBusinessRefType => """
            SELECT r.product_code, r.org_node_code FROM claims.claim_payment_instruction i JOIN claims.claim_register r ON r.tenant_id = i.tenant_id AND r.claim_number = i.claim_number WHERE i.id = @id
            """,
        _ => null,
    };
}
