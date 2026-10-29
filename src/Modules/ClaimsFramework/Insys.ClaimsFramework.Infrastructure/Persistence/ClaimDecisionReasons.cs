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

/// <summary>The claims of policies a fraud indicator referred (Wave 4, F015): counted once per claim.</summary>
internal sealed class ClaimFraudReferrals([FromKeyedServices(ClaimsFrameworkDbContext.SchemaName)] NpgsqlDataSource dataSource) : IClaimFraudReferrals
{
    public async Task<int> ReferredClaims(Guid tenantId, string domain, IReadOnlyCollection<Guid> policyRefs, CancellationToken cancellationToken) =>
        await TenantReads.First(dataSource, tenantId, """
            SELECT count(DISTINCT r.claim_number) FROM claims.claim_register r
            JOIN claims.fraud_indicator_hit h ON h.tenant_id = r.tenant_id AND h.claim_number = r.claim_number
            WHERE r.tenant_id = @t AND r.domain = @domain AND r.policy_ref = ANY(@policies) AND h.referred
            """, r => (object)(int)r.GetInt64(0), cancellationToken, ("t", tenantId), ("domain", domain), ("policies", policyRefs.ToArray())) is int count ? count : 0;
}
