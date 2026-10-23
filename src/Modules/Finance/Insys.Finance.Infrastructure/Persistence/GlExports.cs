/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Finance
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
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Finance.Application;
using Insys.Finance.Domain.Ledger;
using Insys.Finance.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;

namespace Insys.Finance.Infrastructure.Persistence;

/// <summary>GL export batches and their lines (F055), from the posted subledger entries.</summary>
internal sealed class GlExports(FinanceDbContext db) : IGlExports
{
    public async Task<GlExportLayout?> Layout(string layoutCode, DateOnly day, CancellationToken cancellationToken)
    {
        var lifecycle = FinanceStaticData.Descriptors.Single(d => d.EntityType == typeof(GLExportLayouts)).Lifecycle;
        var rows = await db.Set<GLExportLayouts>().AsNoTracking().InForce(lifecycle, day).Where(l => l.LayoutCode == layoutCode).ToListAsync(cancellationToken);

        // A tenant's own layout overrides a global one with the same code.
        return rows.OrderBy(r => r.TenantId is null).FirstOrDefault() is { } row ? new GlExportLayout(row.LayoutCode, row.TargetSystem, row.Format, row.Schedule) : null;
    }

    public async Task<IReadOnlyList<GlExportLine>> Lines(DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken) =>
        await db.Set<SubledgerEntry>().AsNoTracking()
            .Where(e => e.AccountingDate >= fromDate && e.AccountingDate <= toDate)
            .GroupBy(e => new { e.AccountingDate, e.AccountCode, e.Currency })
            .OrderBy(g => g.Key.AccountingDate).ThenBy(g => g.Key.AccountCode).ThenBy(g => g.Key.Currency)
            .Select(g => new GlExportLine(g.Key.AccountingDate, g.Key.AccountCode, g.Key.Currency, g.Sum(e => e.Debit), g.Sum(e => e.Credit), g.Count()))
            .ToListAsync(cancellationToken);

    public Task<bool> Overlaps(string layoutCode, DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken) =>
        db.Set<GlExportBatch>().AnyAsync(b => b.LayoutCode == layoutCode && b.Status != GlExportStatus.Rejected && b.FromDate <= toDate && b.ToDate >= fromDate,
            cancellationToken);

    public Task<GlExportBatch?> Get(Guid batchId, CancellationToken cancellationToken) => db.Set<GlExportBatch>().SingleOrDefaultAsync(b => b.Id == batchId, cancellationToken);

    public async Task<IReadOnlyList<GlExportBatch>> List(string? periodCode, Guid? afterId, int limit, CancellationToken cancellationToken)
    {
        var batches = db.Set<GlExportBatch>().AsNoTracking();
        if (periodCode is not null)
        {
            batches = batches.Where(b => b.PeriodCode == periodCode);
        }

        if (afterId is { } after)
        {
            batches = batches.Where(b => b.Id.CompareTo(after) > 0);
        }

        return await batches.OrderBy(b => b.Id).Take(limit).ToListAsync(cancellationToken);
    }

    public void Add(GlExportBatch batch) => db.Set<GlExportBatch>().Add(batch);
}
