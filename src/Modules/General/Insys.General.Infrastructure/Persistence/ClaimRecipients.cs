/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : General
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
using Insys.General.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.General.Infrastructure.Persistence;

/// <summary>A General claim's notices go to its policy's policyholder (communications §6.3).</summary>
internal sealed class GeneralClaimRecipients([FromKeyedServices(GeneralDbContext.SchemaName)] NpgsqlDataSource dataSource) : IGeneralClaimRecipients
{
    public Task<NoticeRecipient?> ForClaim(Guid tenantId, Guid claimId, CancellationToken cancellationToken) =>
        TenantReads.First(dataSource, tenantId, """
            SELECT p.policyholder_party_id, coalesce(c.claim_number, ''), coalesce(c.product_code, p.product_code), c.org_node_code
            FROM general.claim c JOIN general.policy p ON p.tenant_id = c.tenant_id AND p.id = c.policy_id
            WHERE c.tenant_id = @t AND c.id = @id
            """, r => new NoticeRecipient(r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)),
            cancellationToken, ("t", tenantId), ("id", claimId));
}
