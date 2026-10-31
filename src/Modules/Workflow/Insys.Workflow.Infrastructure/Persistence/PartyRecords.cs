/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Workflow
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

namespace Insys.Workflow.Infrastructure.Persistence;

/// <summary>Cases raised by or for a party, for dormancy and the anonymisation check (OQ-054). A case is final once closed or withdrawn; a reopened one is open again.</summary>
internal sealed class WorkflowPartyRecords([FromKeyedServices(WorkflowDbContext.SchemaName)] NpgsqlDataSource dataSource) : IPartyLinkedRecords
{
    public Task<IReadOnlyList<PartyLinkedRecord>> For(Guid tenantId, Guid partyId, string timeZone, CancellationToken cancellationToken) =>
        PartyRecordReader.Read(dataSource, tenantId, partyId, timeZone, """
        SELECT 'CASE', c.id, 'CASE',
               CASE WHEN c.status IN ('CLOSED', 'WITHDRAWN') THEN (coalesce(c.closed_at, c.updated_at) AT TIME ZONE @zone)::date END
        FROM workflow.service_case c
        WHERE c.complainant_party_id = @party OR c.on_behalf_of_party_id = @party
        """, cancellationToken);
}
