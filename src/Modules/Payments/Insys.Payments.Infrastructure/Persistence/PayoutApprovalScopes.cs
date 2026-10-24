/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Payments
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
using Insys.Payments.Contracts;
using Insys.Workflow.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.Payments.Infrastructure.Persistence;

/// <summary>
/// The scope of a payout batch's approval (F103): when every payment in it pays records of one product and node, that
/// one; otherwise none, so only a checker with unrestricted scope sees and decides it. A batch's approval shows its
/// total, which must not reveal another branch's or product's figures.
/// </summary>
internal sealed class PayoutApprovalScopes([FromKeyedServices(PaymentsDbContext.SchemaName)] NpgsqlDataSource dataSource, IEnumerable<IBusinessRecordScopes> sources)
    : IApprovalScopeSource
{
    public bool Covers(string businessRefType) => businessRefType == PaymentApprovals.PayoutBusinessRefType;

    public async Task<RecordScope?> ScopeOf(Guid tenantId, string businessRefType, Guid businessRefId, CancellationToken cancellationToken)
    {
        var items = await RecordScopeReader.ReadAll(dataSource, tenantId,
            "SELECT business_ref_type, business_ref_id::text FROM payment.payment WHERE payout_batch_id = @id", businessRefId, cancellationToken);
        var scopes = new HashSet<(string?, string?)>();
        foreach (var (type, id) in items)
        {
            if (type is null || id is null || sources.FirstOrDefault(s => s.Covers(type)) is not { } source
                || await source.ScopesOf(tenantId, type, Guid.Parse(id), cancellationToken) is not { } covered)
            {
                return new RecordScope(null, null);
            }

            scopes.UnionWith(covered.Select(c => (c.ProductCode, c.OrgNodeCode)));
        }

        return scopes.Count == 1 ? new RecordScope(scopes.Single().Item1, scopes.Single().Item2) : new RecordScope(null, null);
    }
}
