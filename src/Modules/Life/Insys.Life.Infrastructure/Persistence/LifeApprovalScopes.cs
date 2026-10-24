/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Life
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
using Insys.Life.Contracts;
using Insys.Workflow.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.Life.Infrastructure.Persistence;

/// <summary>The scope of the Life records approvals are about (F103): an underwriting case's proposal, and a claim.</summary>
internal sealed class LifeApprovalScopes([FromKeyedServices(LifeDbContext.SchemaName)] NpgsqlDataSource dataSource) : IApprovalScopeSource
{
    public bool Covers(string businessRefType) => Sql(businessRefType) is not null;

    public async Task<RecordScope?> ScopeOf(Guid tenantId, string businessRefType, Guid businessRefId, CancellationToken cancellationToken) =>
        Sql(businessRefType) is { } sql && await RecordScopeReader.Read(dataSource, tenantId, sql, businessRefId, cancellationToken) is { } scope
            ? new RecordScope(scope.Product, scope.OrgNode)
            : null;

    private static string? Sql(string businessRefType) => businessRefType switch
    {
        LifeApprovals.UnderwritingBusinessRefType => """
            SELECT p.product_code, p.org_node_code FROM life.underwriting_case c JOIN life.proposal p ON p.tenant_id = c.tenant_id AND p.id = c.proposal_id WHERE c.id = @id
            """,
        LifeApprovals.ClaimBusinessRefType => """
            SELECT product_code, org_node_code FROM life.claim WHERE id = @id
            """,
        _ => null,
    };
}
