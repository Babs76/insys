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
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.Life.Infrastructure.Persistence;

/// <summary>Life's policies a party holds, is insured under or benefits from, and its claims a party made or is paid from, for the anonymisation check (D-34). A record's final status is dated by its status history (its last change, for a record older than the history).</summary>
internal sealed class LifePartyRecords([FromKeyedServices(LifeDbContext.SchemaName)] NpgsqlDataSource dataSource) : IPartyLinkedRecords
{
    public Task<IReadOnlyList<PartyLinkedRecord>> For(Guid tenantId, Guid partyId, string timeZone, CancellationToken cancellationToken) =>
        PartyRecordReader.Read(dataSource, tenantId, partyId, timeZone, """
        -- A policy ends when terminated, surrendered, claimed or matured; a lapsed one can be reinstated, so it is open.
        SELECT 'LIFE_POLICY', p.id, 'POLICY',
               CASE WHEN p.status IN ('TERMINATED', 'SURRENDERED', 'CLAIMED', 'MATURED')
                    THEN (coalesce((SELECT max(h.changed_at) FROM life.policy_status_history h WHERE h.tenant_id = p.tenant_id AND h.record_id = p.id AND h.to_status = p.status),
                                   p.updated_at) AT TIME ZONE @zone)::date END
        FROM life.policy p
        WHERE p.policyholder_party_id = @party
           OR EXISTS (SELECT 1 FROM life.policy_insured_life l WHERE l.tenant_id = p.tenant_id AND l.policy_id = p.id AND l.party_id = @party)
           OR EXISTS (SELECT 1 FROM life.beneficiary b WHERE b.tenant_id = p.tenant_id AND b.policy_id = p.id AND b.party_id = @party)
        UNION ALL
        SELECT 'LIFE_CLAIM', c.id, 'CLAIM',
               CASE WHEN c.status IN ('CLOSED', 'DECLINED')
                    THEN (coalesce((SELECT max(h.changed_at) FROM life.claim_status_history h WHERE h.tenant_id = c.tenant_id AND h.record_id = c.id AND h.to_status = c.status),
                                   c.updated_at) AT TIME ZONE @zone)::date END
        FROM life.claim c
        WHERE c.claimant_party_id = @party
           OR EXISTS (SELECT 1 FROM life.claim_payee y WHERE y.tenant_id = c.tenant_id AND y.claim_id = c.id AND y.party_id = @party)
        """, cancellationToken);
}
