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
using Insys.Billing.Application;
using Insys.Billing.Domain.Billing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.Billing.Infrastructure.Persistence;

internal sealed class BillingPortalQueries(BillingDbContext db, IExecutionContext execution) : IBillingPortalQueries
{
    public async Task<IReadOnlyList<Invoice>> Invoices(Guid? payerPartyId, IReadOnlyCollection<Guid> policyRefs, Guid? afterId, int limit,
        CancellationToken cancellationToken)
    {
        var policies = policyRefs.ToList();
        var rows = db.Invoices.AsNoTracking().Where(i => i.DocumentType == DocumentType.Invoice)
            .InScope(execution.CurrentScope, i => i.ProductCode, i => i.OrgNodeCode)
            .Where(i => (payerPartyId != null && db.Accounts.Any(a => a.Id == i.BillingAccountId && a.PayerPartyId == payerPartyId)) || policies.Contains(i.PolicyRef));
        if (afterId is { } after)
        {
            rows = rows.Where(i => i.Id.CompareTo(after) > 0);
        }

        return await rows.OrderBy(i => i.Id).Take(limit).ToListAsync(cancellationToken);
    }

    public Task<Invoice?> Invoice(Guid id, CancellationToken cancellationToken) =>
        db.Invoices.AsNoTracking().Where(i => i.DocumentType == DocumentType.Invoice).InScope(execution.CurrentScope, i => i.ProductCode, i => i.OrgNodeCode)
            .SingleOrDefaultAsync(i => i.Id == id, cancellationToken);

    public Task<Guid?> PayerOf(Guid accountId, CancellationToken cancellationToken) =>
        db.Accounts.AsNoTracking().Where(a => a.Id == accountId).Select(a => (Guid?)a.PayerPartyId).SingleOrDefaultAsync(cancellationToken);
}

/// <summary>An invoice is in an external user's relationship when they pay its account, or broker its policy (external-user access §2).</summary>
internal sealed class BillingPortalRelations([FromKeyedServices(BillingDbContext.SchemaName)] NpgsqlDataSource dataSource, IServiceProvider services) : IPortalRecordRelations
{
    public bool Covers(string businessRefType) => businessRefType == "INVOICE";

    public async Task<bool> Related(Guid tenantId, ExternalSubject who, string businessRefType, Guid businessRefId, CancellationToken cancellationToken)
    {
        if (!Covers(businessRefType))
        {
            return false;
        }

        if (who.Kind == ExternalSubject.Party)
        {
            return await PortalRelationReader.Exists(dataSource, tenantId,
                "SELECT 1 FROM billing.invoice i JOIN billing.billing_account a ON a.tenant_id = i.tenant_id AND a.id = i.billing_account_id WHERE i.id = @id AND a.payer_party_id = @party",
                businessRefId, who, cancellationToken);
        }

        var policy = await PortalRelationReader.Ids(dataSource, tenantId, "SELECT policy_ref FROM billing.invoice WHERE id = @id", [], businessRefId,
            cancellationToken);
        foreach (var domain in services.GetServices<IBrokeredPolicies>())
        {
            if (policy.Count == 1 && await domain.Brokers(tenantId, who.IntermediaryCodes, policy[0], cancellationToken))
            {
                return true;
            }
        }

        return false;
    }
}
