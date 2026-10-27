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
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.Health.Infrastructure.Persistence;

/// <summary>Health's memberships a party is the principal or a dependant of, and its medical claims for a party, for the anonymisation check (D-34). A record's final status is dated by its status history (its last change, for a record older than the history).</summary>
internal sealed class HealthPartyRecords([FromKeyedServices(HealthDbContext.SchemaName)] NpgsqlDataSource dataSource) : IPartyLinkedRecords
{
    public Task<IReadOnlyList<PartyLinkedRecord>> For(Guid tenantId, Guid partyId, string timeZone, CancellationToken cancellationToken) =>
        PartyRecordReader.Read(dataSource, tenantId, partyId, timeZone, """
        SELECT 'HEALTH_MEMBERSHIP', m.id, 'POLICY',
               CASE WHEN m.status IN ('TERMINATED')
                    THEN (coalesce((SELECT max(h.changed_at) FROM health.membership_status_history h WHERE h.tenant_id = m.tenant_id AND h.record_id = m.id AND h.to_status = m.status),
                                   m.updated_at) AT TIME ZONE @zone)::date END
        FROM health.membership m
        WHERE m.principal_party_id = @party
           OR EXISTS (SELECT 1 FROM health.member_dependant d WHERE d.tenant_id = m.tenant_id AND d.membership_id = m.id AND d.party_id = @party)
        UNION ALL
        SELECT 'HEALTH_CLAIM', c.id, 'CLAIM',
               CASE WHEN c.status IN ('PAID', 'REJECTED', 'REVERSED')
                    THEN (coalesce((SELECT max(h.changed_at) FROM health.medical_claim_status_history h WHERE h.tenant_id = c.tenant_id AND h.record_id = c.id AND h.to_status = c.status),
                                   c.updated_at) AT TIME ZONE @zone)::date END
        FROM health.medical_claim c
        WHERE c.patient_party_id = @party
        """, cancellationToken);
}
