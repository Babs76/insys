/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Party
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
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Party.Application;
using Insys.Party.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;
using NodaTime.Text;

namespace Insys.Party.Infrastructure.Persistence;

/// <summary>party.cfg_record_retention_policies in force (OQ-120): the tenant's rows replace the platform's; the longest period wins.</summary>
internal sealed class RecordRetentionPolicyReader(PartyDbContext db) : IRecordRetentionPolicies
{
    public async Task<RetentionLength?> Period(string recordKind, DateOnly day, CancellationToken cancellationToken)
    {
        var lifecycle = PartyStaticData.Descriptors.Single(d => d.EntityType == typeof(RecordRetentionPolicies)).Lifecycle;
        var rows = await db.Set<RecordRetentionPolicies>().AsNoTracking().InForce(lifecycle, day).Where(p => p.RecordKind == recordKind).ToListAsync(cancellationToken);
        var chosen = rows.Any(r => r.TenantId is not null) ? rows.Where(r => r.TenantId is not null) : rows;
        return chosen.Select(r => PeriodPattern.NormalizingIso.Parse(r.Period))
            .Where(p => p.Success)
            .Select(p => new RetentionLength((int)p.Value.Years, (int)p.Value.Months, (int)(p.Value.Weeks * 7 + p.Value.Days)))
            .OrderByDescending(p => p.After(day))
            .FirstOrDefault();
    }
}
