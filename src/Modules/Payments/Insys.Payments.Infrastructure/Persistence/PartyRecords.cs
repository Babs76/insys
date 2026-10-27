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
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.Payments.Infrastructure.Persistence;

/// <summary>Payments to or from a party, for the anonymisation check (D-34). A record's final status is dated by its status history (its last change, for a record older than the history).</summary>
internal sealed class PartyPayments([FromKeyedServices(PaymentsDbContext.SchemaName)] NpgsqlDataSource dataSource) : IPartyLinkedRecords
{
    public Task<IReadOnlyList<PartyLinkedRecord>> For(Guid tenantId, Guid partyId, string timeZone, CancellationToken cancellationToken) =>
        PartyRecordReader.Read(dataSource, tenantId, partyId, timeZone, """
        -- A payment is final once it has an outcome; pending or unknown, it is open.
        SELECT 'PAYMENT', p.id, 'PAYMENT',
               CASE WHEN p.status IN ('SUCCEEDED', 'FAILED', 'REVERSED')
                    THEN (coalesce((SELECT max(h.changed_at) FROM payment.payment_status_history h WHERE h.tenant_id = p.tenant_id AND h.payment_id = p.id AND h.to_status = p.status),
                                   p.updated_at) AT TIME ZONE @zone)::date END
        FROM payment.payment p
        WHERE p.counterparty_party_id = @party
        """, cancellationToken);
}
