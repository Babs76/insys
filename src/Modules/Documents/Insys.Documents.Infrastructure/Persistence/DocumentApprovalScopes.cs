/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Documents
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
using Insys.Workflow.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.Documents.Infrastructure.Persistence;

/// <summary>
/// The scope of a legal hold's release approval (F103, OQ-169, OQ-176): the record the hold is on, as that record's module gives it. A record hold names its
/// record; a document's hold, the record the document is attached to. When the record isn't one a module scopes, or spans several scopes, only a checker
/// with unrestricted scope sees and decides it.
/// </summary>
internal sealed class DocumentApprovalScopes([FromKeyedServices(DocumentsDbContext.SchemaName)] NpgsqlDataSource dataSource, IEnumerable<ILegalHoldTargets> targets)
    : IApprovalScopeSource
{
    public bool Covers(string businessRefType) =>
        businessRefType is Contracts.DocumentApprovals.LegalHoldBusinessRefType or Contracts.DocumentApprovals.RecordHoldBusinessRefType;

    public async Task<RecordScope?> ScopeOf(Guid tenantId, string businessRefType, Guid businessRefId, CancellationToken cancellationToken)
    {
        var held = await RecordScopeReader.Read(dataSource, tenantId, businessRefType == Contracts.DocumentApprovals.RecordHoldBusinessRefType
            ? "SELECT scope_type, scope_id::text FROM document.record_hold WHERE id = @id"
            : """
              SELECT d.business_ref_type, d.business_ref_id::text FROM document.legal_hold h
              JOIN document.document d ON d.tenant_id = h.tenant_id AND d.id = h.scope_id WHERE h.id = @id
              """, businessRefId, cancellationToken);
        if (held is not { Product: { } type, OrgNode: { } id } || targets.FirstOrDefault(t => t.Covers(type)) is not { } target
            || await target.ScopesOf(tenantId, type, Guid.Parse(id), cancellationToken) is not { } scopes)
        {
            return RecordScope.UnrestrictedOnly;
        }

        return scopes.Distinct().ToList() is [var only] ? new RecordScope(only.ProductCode, only.OrgNodeCode) : RecordScope.UnrestrictedOnly;
    }
}
