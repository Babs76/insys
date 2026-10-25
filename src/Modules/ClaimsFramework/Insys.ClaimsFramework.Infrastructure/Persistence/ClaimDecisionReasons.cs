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
using Insys.ClaimsFramework.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.ClaimsFramework.Infrastructure.Persistence;

/// <summary>The tenant's claim decision reasons in force (claims.ref_claim_decision_reason_codes), for the claim modules (OQ-102).</summary>
internal sealed class ClaimDecisionReasons([FromKeyedServices(ClaimsFrameworkDbContext.SchemaName)] NpgsqlDataSource dataSource) : IClaimDecisionReasons
{
    public Task<ClaimDecisionReason?> InForce(Guid tenantId, string code, DateOnly day, CancellationToken cancellationToken) =>
        TenantReads.First(dataSource, tenantId, """
            SELECT code, decision_type, customer_wording FROM claims.ref_claim_decision_reason_codes
            WHERE tenant_id = @t AND code = @code AND record_status IN ('APPROVED', 'ACTIVE', 'DEPRECATED') AND effective_from <= @day
              AND (effective_to IS NULL OR effective_to > @day)
            ORDER BY version DESC LIMIT 1
            """, r => new ClaimDecisionReason(r.GetString(0), r.GetString(1), r.GetString(2)), cancellationToken, ("t", tenantId), ("code", code), ("day", day));
}
