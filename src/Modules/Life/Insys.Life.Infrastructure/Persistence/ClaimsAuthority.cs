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
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Life.Contracts;
using Insys.Workflow.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.Life.Infrastructure.Persistence;

/// <summary>
/// Life claim approval authority: the claims authority limits in force (life.cfg_claims_authority_limits) for the
/// claim's type, by role (Phase 5 review, item 5). Read on Life's own connection.
/// </summary>
internal sealed class ClaimsAuthority([FromKeyedServices(LifeDbContext.SchemaName)] NpgsqlDataSource dataSource) : IApprovalAuthoritySource
{
    public bool Decides(string approvalType) => approvalType == LifeApprovals.ClaimApprovalType;

    public async Task<IReadOnlyList<ApprovalAuthority>> For(Guid tenantId, string businessRefType, Guid businessRefId, IReadOnlyCollection<string> roleCodes,
        DateOnly day, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT a.role, a.limit_value FROM life.cfg_claims_authority_limits a
            JOIN life.claim c ON c.tenant_id = a.tenant_id AND c.id = @claim AND c.claim_type = a.claim_type
            WHERE a.tenant_id = @t AND a.record_status = ANY(@statuses) AND a.effective_from <= @day AND (a.effective_to IS NULL OR a.effective_to > @day)
              AND a.role = ANY(@roles)
            """, connection, transaction);
        command.Parameters.AddWithValue("t", tenantId);
        command.Parameters.AddWithValue("claim", businessRefId);
        command.Parameters.AddWithValue("statuses", StaticLifecycle.Configuration.InForce);
        command.Parameters.AddWithValue("day", day);
        command.Parameters.AddWithValue("roles", roleCodes.ToArray());
        var rows = new List<ApprovalAuthority>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new ApprovalAuthority(reader.GetString(0), reader.GetDecimal(1)));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return rows;
    }

    /// <summary>BR-081: each claims authority limit of the roles, by claim type.</summary>
    public async Task<IReadOnlyList<ListedAuthority>> Listed(Guid tenantId, IReadOnlyCollection<string> roleCodes, DateOnly day, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT a.role, a.claim_type, a.limit_value FROM life.cfg_claims_authority_limits a
            WHERE a.tenant_id = @t AND a.record_status = ANY(@statuses) AND a.effective_from <= @day AND (a.effective_to IS NULL OR a.effective_to > @day)
              AND a.role = ANY(@roles)
            """, connection, transaction);
        command.Parameters.AddWithValue("t", tenantId);
        command.Parameters.AddWithValue("statuses", StaticLifecycle.Configuration.InForce);
        command.Parameters.AddWithValue("day", day);
        command.Parameters.AddWithValue("roles", roleCodes.ToArray());
        var rows = new List<ListedAuthority>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new ListedAuthority(LifeApprovals.ClaimApprovalType, reader.GetString(0), reader.GetDecimal(2), $"claimType={reader.GetString(1)}"));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return rows;
    }
}
