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
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.Health.Contracts;
using Insys.Party.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.Health.Infrastructure.Persistence;

/// <summary>
/// A Health notice goes to the patient; for a dependant under 18 on the day, to the membership's principal (OQ-084), who
/// is responsible for the membership. A patient with no recorded birth date is treated as an adult.
/// </summary>
internal sealed class HealthNoticeRecipients([FromKeyedServices(HealthDbContext.SchemaName)] NpgsqlDataSource dataSource, IPersonFacts persons,
    TenantBusinessDates businessDates) : IHealthNoticeRecipients
{
    public Task<NoticeRecipient?> ForClaim(Guid tenantId, Guid claimId, CancellationToken cancellationToken) =>
        Recipient(tenantId, "health.medical_claim", "claim_number", claimId, cancellationToken);

    public Task<NoticeRecipient?> ForPreauthorisation(Guid tenantId, Guid preauthorisationId, CancellationToken cancellationToken) =>
        Recipient(tenantId, "health.preauthorisation", "auth_number", preauthorisationId, cancellationToken);

    private async Task<NoticeRecipient?> Recipient(Guid tenantId, string table, string referenceColumn, Guid id, CancellationToken cancellationToken)
    {
        var row = await TenantReads.First(dataSource, tenantId, $"""
            SELECT x.patient_party_id, m.principal_party_id, coalesce(x.{referenceColumn}, ''), m.product_code, m.org_node_code
            FROM {table} x JOIN health.membership m ON m.tenant_id = x.tenant_id AND m.id = x.membership_id
            WHERE x.tenant_id = @t AND x.id = @id
            """, r => new Row(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4)),
            cancellationToken, ("t", tenantId), ("id", id));
        if (row is null)
        {
            return null;
        }

        var party = row.Patient;
        if (row.Patient != row.Principal && await persons.BirthDate(tenantId, row.Patient, cancellationToken) is { } born
            && born.AddYears(18) > businessDates.TodayFor(tenantId))
        {
            party = row.Principal;
        }

        return new NoticeRecipient(party, row.Reference, row.ProductCode, row.OrgNodeCode);
    }

    private sealed record Row(Guid Patient, Guid Principal, string Reference, string? ProductCode, string? OrgNodeCode);
}
