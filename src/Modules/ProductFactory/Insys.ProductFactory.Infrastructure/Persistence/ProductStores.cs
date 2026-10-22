using System.Text.Json;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.ProductFactory.Application;
using Insys.ProductFactory.Domain.Products;
using Insys.ProductFactory.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;

namespace Insys.ProductFactory.Infrastructure.Persistence;

/// <summary>
/// Product versions as rows of product.cfg_product_definitions_and_versions (static data, but with the ProductVersion
/// lifecycle: the generic static data writes are not mapped for this table). Each aggregate handed out is copied back
/// to its row before the context saves, and its events go to the product outbox.
/// </summary>
internal sealed class ProductVersionRepository(ProductFactoryDbContext db, ITenantContext tenant) : IProductVersionRepository
{
    private static readonly string[] LockedStatuses = ["APPROVED", "PUBLISHED", "EFFECTIVE", "WITHDRAWN", "RETIRED"];

    private readonly Dictionary<Guid, (ProductVersion Version, ProductDefinitionsAndVersions Row)> _tracked = [];
    private bool _hooked;

    public async Task<ProductVersion?> Get(Guid id, CancellationToken cancellationToken) =>
        _tracked.TryGetValue(id, out var tracked) ? tracked.Version : Track(await Rows().SingleOrDefaultAsync(r => r.Id == id, cancellationToken));

    public async Task<ProductVersion?> Get(string productCode, int version, CancellationToken cancellationToken) =>
        _tracked.Values.FirstOrDefault(t => t.Version.ProductCode == productCode && t.Version.Version == version).Version
        ?? Track(await Rows().SingleOrDefaultAsync(r => r.Code == productCode && r.Version == version, cancellationToken));

    public async Task<IReadOnlyList<ProductVersion>> Versions(string productCode, CancellationToken cancellationToken) =>
        (await Rows().Where(r => r.Code == productCode).OrderByDescending(r => r.Version).ToListAsync(cancellationToken))
        .Select(r => _tracked.TryGetValue(r.Id, out var t) ? t.Version : Track(r)!).ToList();

    public async Task<IReadOnlyList<Guid>> InStatus(ProductVersionStatus status, DateOnly? effectiveOnOrBefore, int limit, CancellationToken cancellationToken)
    {
        var code = UpperCaseEnumConverter<ProductVersionStatus>.ToCode(status);
        var rows = Rows().AsNoTracking().Where(r => r.RecordStatus == code);
        if (effectiveOnOrBefore is { } day)
        {
            rows = rows.Where(r => r.EffectiveFrom <= day);
        }

        return await rows.OrderBy(r => r.Id).Select(r => r.Id).Take(limit).ToListAsync(cancellationToken);
    }

    public void Add(ProductVersion version)
    {
        var row = new ProductDefinitionsAndVersions { Id = version.Id, TenantId = tenant.RequireTenantId(), ChangeReason = "Product version" };
        db.Set<ProductDefinitionsAndVersions>().Add(row);
        Remember(version, row);
    }

    private IQueryable<ProductDefinitionsAndVersions> Rows() => db.Set<ProductDefinitionsAndVersions>().Where(r => r.TenantId != null);

    private ProductVersion? Track(ProductDefinitionsAndVersions? row)
    {
        if (row is null)
        {
            return null;
        }

        var version = ProductVersion.Rehydrate(new ProductVersionState(row.Id, row.Code, row.Version, Enum.Parse<ProductDomain>(row.Domain, ignoreCase: true), row.Name,
            row.Coverages, UpperCaseEnumConverter<ProductVersionStatus>.FromCode(row.RecordStatus), row.EffectiveFrom, row.EffectiveTo, row.SupersedesId,
            row.FamilyCode, row.ClassCode, row.ApprovedBy, row.ApprovedAt, row.RowVersion, UpperCaseEnumConverter<CoverStart>.FromCode(row.CoverStartsOn)));
        Remember(version, row);
        return version;
    }

    private void Remember(ProductVersion version, ProductDefinitionsAndVersions row)
    {
        _tracked[version.Id] = (version, row);
        if (!_hooked)
        {
            _hooked = true;
            db.BeforeSave(CopyToRows);
        }
    }

    private void CopyToRows()
    {
        foreach (var (version, row) in _tracked.Values.Where(t => t.Version.HasChanges))
        {
            var status = UpperCaseEnumConverter<ProductVersionStatus>.ToCode(version.Status);
            var stored = db.Entry(row).State == EntityState.Added ? null : db.Entry(row).Property(r => r.RecordStatus).OriginalValue;
            if (stored is not null && LockedStatuses.Contains(stored))
            {
                // sdm.guard_static_version: an approved row changes only its record_status and effective_to (SDM-002).
                row.RecordStatus = status;
                row.EffectiveTo = version.EffectiveTo;
                Publish(version);
                continue;
            }

            row.Code = version.ProductCode;
            row.Version = version.Version;
            row.ProductCode = version.ProductCode;
            row.Domain = version.Domain.ToString().ToUpperInvariant();
            row.Name = version.Name;
            row.Coverages = version.Coverages.ToArray();
            row.RecordStatus = status;
            row.EffectiveFrom = version.EffectiveFrom;
            row.EffectiveTo = version.EffectiveTo;
            row.SupersedesId = version.SupersedesId;
            row.ApprovedBy = version.ApprovedBy;
            row.ApprovedAt = version.ApprovedAt;
            row.FamilyCode = version.FamilyCode;
            row.ClassCode = version.ClassCode;
            row.CoverStartsOn = UpperCaseEnumConverter<CoverStart>.ToCode(version.CoverStartsOn);
            Publish(version);
        }
    }

    private void Publish(ProductVersion version)
    {
        foreach (var domainEvent in ((IAggregateRoot)version).DequeueDomainEvents())
        {
            if (ProductEventMapper.Map(domainEvent) is { } integrationEvent)
            {
                db.Publish(integrationEvent, nameof(ProductVersion), version.Id, domainEvent.OccurredAt);
            }
        }

        ((IAggregateRoot)version).AcceptChanges();
    }

}

internal sealed class SimulationRunStore(ProductFactoryDbContext db) : ISimulationRuns
{
    public Task<SimulationRun?> Get(Guid id, CancellationToken cancellationToken) =>
        db.SimulationRuns.Include(r => r.Results).SingleOrDefaultAsync(r => r.Id == id, cancellationToken);

    public async Task<IReadOnlyList<SimulationRun>> For(string productCode, int version, CancellationToken cancellationToken) =>
        await db.SimulationRuns.Include(r => r.Results).Where(r => r.ProductCode == productCode && r.ProductVersion == version)
            .OrderByDescending(r => r.StartedAt).ThenByDescending(r => r.Id).ToListAsync(cancellationToken);

    public void Add(SimulationRun run) => db.SimulationRuns.Add(run);
}

internal sealed class ProductCatalogue(ProductFactoryDbContext db, IProductVersionRepository versions) : IProductCatalogue
{
    public async Task<bool> FamilyExists(ProductDomain domain, string familyCode, string? classCode, DateOnly day, CancellationToken cancellationToken)
    {
        var line = domain.ToString().ToUpperInvariant();
        return await InForce<ProductTaxonomy>(day)
            .AnyAsync(t => t.Line == line && t.FamilyCode == familyCode && (classCode == null || t.ClassCode == classCode), cancellationToken);
    }

    public async Task<ProductValidationContext> ValidationContext(ProductVersion version, DateOnly day, CancellationToken cancellationToken)
    {
        var coverages = (await InForce<CoverageAndBenefitLibrary>(day).ToListAsync(cancellationToken))
            .GroupBy(c => c.Code).ToDictionary(g => g.Key, g => new CoverageDefinition(g.Key, Enum.Parse<ProductDomain>(g.First().Domain, ignoreCase: true)), StringComparer.Ordinal);
        var dependencies = (await InForce<BenefitDependencyRules>(day).ToListAsync(cancellationToken))
            .Select(d => new BenefitDependency(d.Benefit, d.Relation, d.TargetBenefit)).ToList();
        var superseded = version.SupersedesId is { } id ? await versions.Get(id, cancellationToken) : null;
        return new ProductValidationContext(coverages, dependencies, day, superseded?.EffectiveFrom);
    }

    public async Task<bool> SimulationRequired(DateOnly day, CancellationToken cancellationToken)
    {
        // Tenant settings over the global baseline (scope GT); simulation is required when nothing says otherwise (PRD-005).
        var settings = await InForce<ProductApprovalWorkflowSettings>(day).OrderByDescending(s => s.TenantId != null).FirstOrDefaultAsync(cancellationToken);
        return settings?.SimulationRequired ?? true;
    }

    private IQueryable<T> InForce<T>(DateOnly day) where T : StaticRecord =>
        db.Set<T>().AsNoTracking().InForce(ProductFactoryStaticData.Descriptors.Single(d => d.EntityType == typeof(T)).Lifecycle, day);
}

internal static class ProductEventMapper
{
    public static IIntegrationEvent? Map(IDomainEvent domainEvent) => domainEvent switch
    {
        ProductRegistered e => new Contracts.ProductRegistered(e.VersionId, e.ProductCode, Upper(e.Domain), e.FamilyCode),
        ProductVersionValidated e => new Contracts.ProductVersionValidated(e.VersionId, e.ProductCode, e.Version),
        ProductVersionSubmitted e => new BuildingBlocks.Contracts.Approvals.ApprovalRequested(Contracts.ProductApprovals.BusinessRefType, e.VersionId,
            Contracts.ProductApprovals.ApprovalType, e.MakerId, null, null, $"{e.ProductCode} v{e.Version}: {e.Reason}"),
        ProductVersionApproved e => new Contracts.ProductVersionApproved(e.VersionId, e.ProductCode, e.Version, e.CheckerId),
        ProductVersionPublished e => new Contracts.ProductVersionPublished(e.VersionId, e.ProductCode, e.Version, Upper(e.Domain), e.EffectiveFrom),
        ProductVersionWithdrawn e => new Contracts.ProductVersionWithdrawn(e.VersionId, e.ProductCode, e.Version, e.EffectiveTo),
        _ => null,
    };

    private static string Upper(ProductDomain domain) => domain.ToString().ToUpperInvariant();
}
