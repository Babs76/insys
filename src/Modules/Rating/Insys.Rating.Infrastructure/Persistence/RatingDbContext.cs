using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.Rating.Domain.Calculations;
using Insys.Rating.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;

namespace Insys.Rating.Infrastructure.Persistence;

/// <summary>The Rating module's context: its own schema only, on its own database role (DBA-004).</summary>
public sealed class RatingDbContext(DbContextOptions<RatingDbContext> options, ModuleDbContextDependencies dependencies)
    : ModuleDbContext(options, dependencies)
{
    public const string SchemaName = "rating";

    public override string Schema => SchemaName;

    public DbSet<PremiumCalculation> Calculations => Set<PremiumCalculation>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        RatingStaticData.Configure(modelBuilder);

        // rating_request is partitioned by calculated_at, which is part of its key; result lines reference it by id only
        // (no foreign key to a partitioned table, V0001). Neither is ever updated.
        modelBuilder.Entity<PremiumCalculation>(calculation =>
        {
            calculation.ToTable("rating_request");
            calculation.HasKey(c => new { c.TenantId, c.Id, c.CalculatedAt });
            calculation.HasAlternateKey(c => new { c.TenantId, c.Id });
            calculation.HasAuditColumns(updatable: false);
            calculation.Ignore(c => c.DomainEvents);
            calculation.Ignore(c => c.HasChanges);
            calculation.Ignore(c => c.RowVersion);
            calculation.Property(c => c.RequestRef).HasMaxLength(30);
            calculation.Property(c => c.SourceDomain).HasMaxLength(10);
            calculation.Property(c => c.SubjectType).HasMaxLength(30);
            calculation.Property(c => c.ProductCode).HasMaxLength(15);
            calculation.Property(c => c.Inputs).HasColumnType("jsonb");
            calculation.Property(c => c.Premium).HasPrecision(18, 2);
            calculation.Property(c => c.Currency).HasColumnType("char(3)");
            calculation.HasMany(c => c.Lines).WithOne().HasForeignKey(l => new { l.TenantId, l.RequestId }).HasPrincipalKey(c => new { c.TenantId, c.Id });
            calculation.Navigation(c => c.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<ResultLine>(line =>
        {
            line.ToTable("rating_result_line");
            line.HasKey(l => new { l.TenantId, l.Id });
            line.HasAuditColumns(updatable: false);
            line.Property(l => l.Component).HasMaxLength(40);
            line.Property(l => l.FactorCode).HasMaxLength(20);
            line.Property(l => l.FactorValue).HasMaxLength(40);
            line.Property(l => l.Amount).HasPrecision(18, 6);
        });
    }
}
