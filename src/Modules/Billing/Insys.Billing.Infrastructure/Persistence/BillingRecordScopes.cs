/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Billing
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

namespace Insys.Billing.Infrastructure.Persistence;

/// <summary>
/// What an invoice, an account or a refund covers (F103, OQ-081), for payments held against them: an invoice its own
/// product and node; an account, and a refund from it, every schedule on the account.
/// </summary>
internal sealed class BillingRecordScopes([FromKeyedServices(BillingDbContext.SchemaName)] NpgsqlDataSource dataSource) : IBusinessRecordScopes
{
    private const string AccountSchedules = "SELECT product_code, org_node_code FROM billing.premium_schedule WHERE billing_account_id = @id";

    public bool Covers(string businessRefType) => Sql(businessRefType) is not null;

    public async Task<IReadOnlyList<(string? ProductCode, string? OrgNodeCode)>?> ScopesOf(Guid tenantId, string businessRefType, Guid businessRefId,
        CancellationToken cancellationToken)
    {
        if (Sql(businessRefType) is not { } sql)
        {
            return null;
        }

        var scopes = await RecordScopeReader.ReadAll(dataSource, tenantId, sql, businessRefId, cancellationToken);
        return scopes.Select(s => ((string?)s.Product, (string?)s.OrgNode)).ToList();
    }

    private static string? Sql(string businessRefType) => businessRefType switch
    {
        "INVOICE" => "SELECT product_code, org_node_code FROM billing.invoice WHERE id = @id",
        "BILLING_ACCOUNT" => AccountSchedules,
        Contracts.BillingApprovals.RefundBusinessRefType =>
            "SELECT s.product_code, s.org_node_code FROM billing.refund_request r JOIN billing.premium_schedule s ON s.tenant_id = r.tenant_id AND s.billing_account_id = r.billing_account_id WHERE r.id = @id",
        _ => null,
    };
}
