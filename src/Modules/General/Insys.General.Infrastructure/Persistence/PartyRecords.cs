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
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.General.Infrastructure.Persistence;

/// <summary>General's policies a party holds and its claims a party is named on, for the anonymisation check (D-34). A record's final status is dated by its status history (its last change, for a record older than the history).</summary>
internal sealed class GeneralPartyRecords([FromKeyedServices(GeneralDbContext.SchemaName)] NpgsqlDataSource dataSource) : IPartyLinkedRecords
{
    public Task<IReadOnlyList<PartyLinkedRecord>> For(Guid tenantId, Guid partyId, string timeZone, CancellationToken cancellationToken) =>
        PartyRecordReader.Read(dataSource, tenantId, partyId, timeZone, """
        -- A policy ends when cancelled, expired or renewed (the renewal is a policy of its own).
        SELECT 'GENERAL_POLICY', p.id, 'POLICY',
               CASE WHEN p.status IN ('CANCELLED', 'EXPIRED', 'RENEWED')
                    THEN (coalesce((SELECT max(h.changed_at) FROM general.policy_status_history h WHERE h.tenant_id = p.tenant_id AND h.record_id = p.id AND h.to_status = p.status),
                                   p.updated_at) AT TIME ZONE @zone)::date END
        FROM general.policy p
        WHERE p.policyholder_party_id = @party
        UNION ALL
        -- A closed claim can be reopened; while reopened it is open again.
        SELECT 'GENERAL_CLAIM', c.id, 'CLAIM',
               CASE WHEN c.status IN ('CLOSED', 'DECLINED')
                    THEN (coalesce((SELECT max(h.changed_at) FROM general.claim_status_history h WHERE h.tenant_id = c.tenant_id AND h.record_id = c.id AND h.to_status = c.status),
                                   c.updated_at) AT TIME ZONE @zone)::date END
        FROM general.claim c
        WHERE EXISTS (SELECT 1 FROM general.claim_party x WHERE x.tenant_id = c.tenant_id AND x.claim_id = c.id AND x.party_id = @party)
        """, cancellationToken);
}
