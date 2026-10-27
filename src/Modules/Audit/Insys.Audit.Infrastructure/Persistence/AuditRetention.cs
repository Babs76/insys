/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Audit
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
using Insys.Audit.Contracts;
using Insys.Audit.Infrastructure.StaticData;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;
using NodaTime.Text;

namespace Insys.Audit.Infrastructure.Persistence;

/// <summary>Retention periods (audit.cfg_audit_retention_policies) for other modules' retention jobs, in their own read transaction.</summary>
internal sealed class AuditRetention(AuditDbContext db) : IAuditRetention
{
    public async Task<RetentionPeriod?> Period(string eventClass, DateOnly day, CancellationToken cancellationToken)
    {
        var lifecycle = AuditStaticData.Descriptors.Single(d => d.EntityType == typeof(AuditRetentionPolicies)).Lifecycle;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var rows = await db.Set<AuditRetentionPolicies>().AsNoTracking().InForce(lifecycle, day).Where(p => p.EventClass == eventClass).ToListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var chosen = rows.Any(r => r.TenantId is not null) ? rows.Where(r => r.TenantId is not null) : rows;
        return chosen.Select(r => PeriodPattern.NormalizingIso.Parse(r.Period).GetValueOrThrow())
            .Select(p => new RetentionPeriod((int)p.Years, (int)p.Months, (int)(p.Weeks * 7 + p.Days)))
            .OrderBy(p => p.Before(DateTimeOffset.UtcNow)) // the earliest cut-off is the longest period
            .FirstOrDefault();
    }
}

/// <summary>The latest audit event about a target (D-34), on the audit index by tenant and target, in its own read transaction.</summary>
internal sealed class AuditedTargets(AuditDbContext db) : IAuditedTargets
{
    public async Task<DateTimeOffset?> LatestAbout(string targetType, string targetId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var latest = await db.AuditEvents.AsNoTracking().Where(e => e.TargetType == targetType && e.TargetId == targetId)
            .MaxAsync(e => (DateTimeOffset?)e.OccurredAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return latest;
    }
}
