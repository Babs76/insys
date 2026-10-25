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
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.Health.Contracts;
using Insys.Workflow.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.Health.Infrastructure.Persistence;

/// <summary>
/// What a Health remittance pays (F103), for the payment and payout batch item that pay it: the product and node of every
/// claim it remits, by their memberships. A member's own claims are remitted under their member number.
/// </summary>
internal sealed class HealthRemittanceScopes([FromKeyedServices(HealthDbContext.SchemaName)] NpgsqlDataSource dataSource) : IBusinessRecordScopes
{
    public bool Covers(string businessRefType) => businessRefType == HealthReferences.RemittanceRefType;

    public async Task<IReadOnlyList<(string? ProductCode, string? OrgNodeCode)>?> ScopesOf(Guid tenantId, string businessRefType, Guid businessRefId,
        CancellationToken cancellationToken) =>
        Covers(businessRefType)
            ? (await RecordScopeReader.ReadAll(dataSource, tenantId, """
                SELECT DISTINCT m.product_code, m.org_node_code FROM health.remittance_advice r
                JOIN health.medical_claim c ON c.tenant_id = r.tenant_id AND c.payment_run_id = r.run_id
                JOIN health.membership m ON m.tenant_id = c.tenant_id AND m.id = c.membership_id
                WHERE r.id = @id AND ((c.source = 'MEMBER' AND m.member_number = r.provider_id) OR (c.source <> 'MEMBER' AND c.provider_id = r.provider_id))
                """, businessRefId, cancellationToken)).Select(s => (s.Product, s.OrgNode)).ToList()
            : null;
}

/// <summary>
/// The scope of a payment run's approval (F103): when every claim it pays shares one product and node, that one;
/// otherwise none, so only a checker with unrestricted scope sees and decides it (the approval shows the run's total).
/// </summary>
internal sealed class PaymentRunApprovalScopes([FromKeyedServices(HealthDbContext.SchemaName)] NpgsqlDataSource dataSource) : IApprovalScopeSource
{
    public bool Covers(string businessRefType) => businessRefType == HealthApprovals.RunBusinessRefType;

    public async Task<RecordScope?> ScopeOf(Guid tenantId, string businessRefType, Guid businessRefId, CancellationToken cancellationToken)
    {
        var scopes = await RecordScopeReader.ReadAll(dataSource, tenantId, """
            SELECT DISTINCT m.product_code, m.org_node_code FROM health.medical_claim c
            JOIN health.membership m ON m.tenant_id = c.tenant_id AND m.id = c.membership_id WHERE c.payment_run_id = @id
            """, businessRefId, cancellationToken);
        return scopes is [var only] ? new RecordScope(only.Product, only.OrgNode) : new RecordScope(null, null);
    }
}

/// <summary>A provider's standing in the register (health.mst_provider_register), on Health's own connection.</summary>
internal sealed class ProviderDirectory([FromKeyedServices(HealthDbContext.SchemaName)] NpgsqlDataSource dataSource) : IProviderDirectory
{
    public async Task<bool> IsActive(Guid tenantId, string providerId, DateOnly day, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT status = 'ACTIVE' FROM health.mst_provider_register
            WHERE tenant_id = @t AND provider_id = @p AND record_status IN ('APPROVED', 'ACTIVE', 'DEPRECATED') AND effective_from <= @day
              AND (effective_to IS NULL OR effective_to > @day)
            ORDER BY version DESC LIMIT 1
            """, connection, transaction);
        command.Parameters.AddWithValue("t", tenantId);
        command.Parameters.AddWithValue("p", providerId);
        command.Parameters.AddWithValue("day", day);
        var active = await command.ExecuteScalarAsync(cancellationToken) is true;
        await transaction.CommitAsync(cancellationToken);
        return active;
    }

    public async Task<Guid?> PartyOf(Guid tenantId, string providerId, DateOnly day, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT party_id FROM health.mst_provider_register
            WHERE tenant_id = @t AND provider_id = @p AND record_status IN ('APPROVED', 'ACTIVE', 'DEPRECATED') AND effective_from <= @day
              AND (effective_to IS NULL OR effective_to > @day)
            ORDER BY version DESC LIMIT 1
            """, connection, transaction);
        command.Parameters.AddWithValue("t", tenantId);
        command.Parameters.AddWithValue("p", providerId);
        command.Parameters.AddWithValue("day", day);
        var party = await command.ExecuteScalarAsync(cancellationToken) is Guid id ? id : (Guid?)null;
        await transaction.CommitAsync(cancellationToken);
        return party;
    }
}
