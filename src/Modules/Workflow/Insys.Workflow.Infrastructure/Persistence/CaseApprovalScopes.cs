/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Workflow
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

namespace Insys.Workflow.Infrastructure.Persistence;

/// <summary>
/// The scope of a case's redress approval (F103, OQ-176): the case's product and node. A case placed nowhere, about no scoped record, is decided only by a
/// checker with unrestricted scope, as nothing shows whose it is.
/// </summary>
internal sealed class CaseApprovalScopes([FromKeyedServices(WorkflowDbContext.SchemaName)] NpgsqlDataSource dataSource) : IApprovalScopeSource
{
    public bool Covers(string businessRefType) => businessRefType == CaseApprovals.RedressBusinessRefType;

    public async Task<RecordScope?> ScopeOf(Guid tenantId, string businessRefType, Guid businessRefId, CancellationToken cancellationToken) =>
        await RecordScopeReader.Read(dataSource, tenantId, """
            SELECT c.product_code, c.org_node_code FROM workflow.case_redress r JOIN workflow.service_case c ON c.tenant_id = r.tenant_id AND c.id = r.case_id WHERE r.id = @id
            """, businessRefId, cancellationToken) is { } scope && (scope.Product is not null || scope.OrgNode is not null)
            ? new RecordScope(scope.Product, scope.OrgNode)
            : RecordScope.UnrestrictedOnly;
}
