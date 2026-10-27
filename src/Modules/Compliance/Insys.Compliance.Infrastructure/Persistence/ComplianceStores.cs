/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Compliance
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
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Compliance.Application;
using Insys.Compliance.Domain;
using Insys.Compliance.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;

namespace Insys.Compliance.Infrastructure.Persistence;

internal sealed class ComplianceRepository(ComplianceDbContext db) : IComplianceRepository
{
    public Task<RegulatoryReturn?> Return(Guid id, CancellationToken cancellationToken) => db.Returns.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);

    public async Task<RegulatoryReturn?> Return(string returnCode, string periodCode, CancellationToken cancellationToken) =>
        db.ChangeTracker.Entries<RegulatoryReturn>().Select(e => e.Entity).FirstOrDefault(r => r.ReturnCode == returnCode && r.PeriodCode == periodCode)
        ?? await db.Returns.SingleOrDefaultAsync(r => r.ReturnCode == returnCode && r.PeriodCode == periodCode, cancellationToken);

    public async Task<IReadOnlyList<(string ReturnCode, string PeriodCode, ReturnStatus Status)>> ReturnStatuses(string fromPeriod, string toPeriod,
        CancellationToken cancellationToken) =>
        (await db.Returns.AsNoTracking().Where(r => EF.Functions.Collate(r.PeriodCode, "C").CompareTo(fromPeriod) >= 0 && EF.Functions.Collate(r.PeriodCode, "C").CompareTo(toPeriod) <= 0)
            .Select(r => new { r.ReturnCode, r.PeriodCode, r.Status }).ToListAsync(cancellationToken))
        .Select(r => (r.ReturnCode, r.PeriodCode, r.Status)).ToList();

    public async Task<IReadOnlyList<RegulatoryReturn>> Returns(string? periodCode, ReturnStatus? status, CancellationToken cancellationToken)
    {
        var query = db.Returns.AsNoTracking();
        if (periodCode is not null)
        {
            query = query.Where(r => r.PeriodCode == periodCode);
        }

        if (status is { } s)
        {
            query = query.Where(r => r.Status == s);
        }

        return await query.OrderBy(r => r.PeriodCode).ThenBy(r => r.ReturnCode).Take(500).ToListAsync(cancellationToken);
    }

    public Task<ReportRun?> Run(Guid id, CancellationToken cancellationToken) => db.Runs.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);

    public async Task<IReadOnlyList<ReportRunSummary>> Runs(string? reportCode, DateTimeOffset? from, DateTimeOffset? to, int limit, CancellationToken cancellationToken)
    {
        var query = db.Runs.AsNoTracking();
        if (reportCode is not null)
        {
            query = query.Where(r => r.ReportCode == reportCode);
        }

        if (from is { } after)
        {
            query = query.Where(r => EF.Property<DateTimeOffset>(r, "CreatedAt") >= after);
        }

        if (to is { } before)
        {
            query = query.Where(r => EF.Property<DateTimeOffset>(r, "CreatedAt") < before);
        }

        return await query.OrderByDescending(r => EF.Property<DateTimeOffset>(r, "CreatedAt")).ThenByDescending(r => r.Id).Take(limit)
            .Select(r => new ReportRunSummary(r.Id, r.ReportCode, r.RequestedBy, EF.Property<DateTimeOffset>(r, "CreatedAt"), r.Status, r.Exported))
            .ToListAsync(cancellationToken);
    }

    public void Add(RegulatoryReturn regulatoryReturn) => db.Returns.Add(regulatoryReturn);

    public void Add(ReportRun run) => db.Runs.Add(run);
}

internal sealed class ComplianceCodes(ComplianceDbContext db) : IComplianceCodes
{
    public async Task<ReturnDefinition?> Definition(string returnCode, DateOnly day, CancellationToken cancellationToken) =>
        (await InForce<RegulatoryReturnDefinitions>(day).Where(d => d.ReturnCode == returnCode).OrderByDescending(d => d.Version).FirstOrDefaultAsync(cancellationToken))
        is { } d ? new ReturnDefinition(d.ReturnCode, d.Regulator, d.FieldMapping, d.Frequency, d.SubmissionDays) : null;

    public async Task<IReadOnlyList<ReturnDefinition>> Definitions(DateOnly day, CancellationToken cancellationToken) =>
        (await InForce<RegulatoryReturnDefinitions>(day).OrderBy(d => d.ReturnCode).ToListAsync(cancellationToken))
        .Select(d => new ReturnDefinition(d.ReturnCode, d.Regulator, d.FieldMapping, d.Frequency, d.SubmissionDays)).ToList();

    public async Task<ReportDefinition?> Report(string reportCode, DateOnly day, CancellationToken cancellationToken) =>
        await InForce<ReportCatalogueAndAccessRules>(day).Where(r => r.ReportCode == reportCode).OrderByDescending(r => r.Version)
            .Select(r => new ReportDefinition(r.ReportCode, r.Name, r.Roles, r.ExportAllowed)).FirstOrDefaultAsync(cancellationToken);

    private IQueryable<T> InForce<T>(DateOnly day) where T : StaticRecord =>
        db.Set<T>().AsNoTracking().InForce(ComplianceStaticData.Descriptors.Single(d => d.EntityType == typeof(T)).Lifecycle, day);
}

internal sealed class ComplianceEventMapper : IDomainEventMapper
{
    public IIntegrationEvent? Map(IDomainEvent domainEvent) => domainEvent switch
    {
        Domain.RegulatoryReturnSubmitted e => new Contracts.RegulatoryReturnSubmitted(e.ReturnId, e.ReturnCode, e.PeriodCode, e.Regulator, e.RegulatorRef),
        _ => null,
    };
}
