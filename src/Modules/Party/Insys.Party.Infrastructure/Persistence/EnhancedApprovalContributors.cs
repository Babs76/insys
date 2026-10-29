/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Party
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
using Insys.Party.Contracts;
using Insys.Workflow.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.Party.Infrastructure.Persistence;

/// <summary>
/// BR-109 (Ade, 30 October 2026): nobody who contributed to a party's current assessment may give its enhanced due
/// diligence approval, whatever order the results arrived in. A contributor recorded a check or reviewed a hit since the
/// party was last cleared (or ever, if it never was). The requester is the maker, refused by Workflow as for any approval.
/// </summary>
internal sealed class EnhancedApprovalContributors([FromKeyedServices(PartyDbContext.SchemaName)] NpgsqlDataSource dataSource) : IApprovalCheckerExclusions
{
    public bool Covers(string approvalType) => approvalType == PartyApprovals.EnhancedDueDiligenceType;

    public async Task<bool> Excludes(Guid tenantId, string businessRefType, Guid businessRefId, Guid checkerId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            WITH dd AS (SELECT id, cleared_at FROM party.due_diligence WHERE tenant_id = @t AND party_id = @p)
            SELECT EXISTS (SELECT 1 FROM party.due_diligence_check c LEFT JOIN dd ON true
                           WHERE c.tenant_id = @t AND c.party_id = @p AND c.checked_by = @u AND (dd.cleared_at IS NULL OR c.checked_at > dd.cleared_at))
                OR EXISTS (SELECT 1 FROM party.screening_hit h JOIN dd ON dd.id = h.due_diligence_id
                           WHERE h.tenant_id = @t AND h.reviewed_by = @u AND (dd.cleared_at IS NULL OR h.reviewed_at > dd.cleared_at))
            """, connection, transaction);
        command.Parameters.AddWithValue("t", tenantId);
        command.Parameters.AddWithValue("p", businessRefId);
        command.Parameters.AddWithValue("u", checkerId);
        var excluded = (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
        await transaction.CommitAsync(cancellationToken);
        return excluded;
    }
}
