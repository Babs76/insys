using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.ProductFactory.Domain.Products;
using Insys.ProductFactory.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;

namespace Insys.ProductFactory.Infrastructure.Persistence;

/// <summary>The ProductFactory module's context: its own schema only, on its own database role (DBA-004).</summary>
public sealed class ProductFactoryDbContext(DbContextOptions<ProductFactoryDbContext> options, ModuleDbContextDependencies dependencies)
    : ModuleDbContext(options, dependencies)
{
    public const string SchemaName = "product";

    private readonly List<Action> _beforeSave = [];

    public override string Schema => SchemaName;

    public DbSet<SimulationRun> SimulationRuns => Set<SimulationRun>();

    /// <summary>Product versions are aggregates kept in static data rows; their repository copies them to the rows here.</summary>
    internal void BeforeSave(Action action) => _beforeSave.Add(action);

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        foreach (var action in _beforeSave)
        {
            action();
        }

        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        ProductFactoryStaticData.Configure(modelBuilder);

        modelBuilder.Entity<SimulationRun>(run =>
        {
            run.ToTable("simulation_run");
            run.HasKey(r => new { r.TenantId, r.Id });
            run.HasAuditColumns();
            run.Ignore(r => r.DomainEvents);
            run.Ignore(r => r.HasChanges);
            run.Property(r => r.RunNumber).HasMaxLength(20);
            run.Property(r => r.ProductCode).HasMaxLength(15);
            run.Property(r => r.Status).HasConversion(new UpperCaseEnumConverter<SimulationStatus>()).HasMaxLength(10);
            run.HasMany(r => r.Results).WithOne().HasForeignKey(r => new { r.TenantId, r.RunId });
            run.Navigation(r => r.Results).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<SimulationResult>(result =>
        {
            result.ToTable("simulation_result");
            result.HasKey(r => new { r.TenantId, r.Id });
            result.HasAuditColumns(updatable: false);
            result.Property(r => r.CaseId).HasMaxLength(20);
            result.Property(r => r.Expected).HasColumnType("jsonb");
            result.Property(r => r.Actual).HasColumnType("jsonb");
        });
    }
}
