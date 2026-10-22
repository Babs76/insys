using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.Reinsurance.Domain;
using Insys.Reinsurance.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;

namespace Insys.Reinsurance.Infrastructure.Persistence;

/// <summary>The Reinsurance module's context: its own schema only, on its own database role (DBA-004).</summary>
public sealed class ReinsuranceDbContext(DbContextOptions<ReinsuranceDbContext> options, ModuleDbContextDependencies dependencies)
    : ModuleDbContext(options, dependencies)
{
    public const string SchemaName = "reinsurance";

    public override string Schema => SchemaName;

    public DbSet<Cession> Cessions => Set<Cession>();

    public DbSet<ReinsuranceRecovery> Recoveries => Set<ReinsuranceRecovery>();

    public DbSet<Bordereau> Bordereaux => Set<Bordereau>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        ReinsuranceStaticData.Configure(modelBuilder);

        // Insert-only: a recalculation is a new cession superseding the old one (REI-002). No row_version.
        modelBuilder.Entity<Cession>(cession =>
        {
            cession.ToTable("cession");
            cession.HasKey(c => new { c.TenantId, c.Id });
            cession.HasAuditColumns(updatable: false);
            cession.Ignore(c => c.DomainEvents);
            cession.Ignore(c => c.HasChanges);
            cession.Ignore(c => c.RowVersion);
            cession.Ignore(c => c.SourceDomain);
            cession.Property(c => c.TreatyCode).HasMaxLength(15);
            cession.Property(c => c.ArrangementCode).HasMaxLength(15);
            cession.Property(c => c.SourceType).HasMaxLength(20);
            cession.Property(c => c.ProductCode).HasMaxLength(15);
            cession.Property(c => c.GrossSumInsured).HasPrecision(20, 2);
            cession.Property(c => c.GrossPremium).HasPrecision(18, 2);
            cession.Property(c => c.CededShare).HasPrecision(9, 6);
            cession.Property(c => c.CededSumInsured).HasPrecision(20, 2);
            cession.Property(c => c.CededPremium).HasPrecision(18, 2);
            cession.Property(c => c.Currency).HasColumnType("char(3)");
            cession.Property(c => c.RuleCode).HasMaxLength(15);
        });

        modelBuilder.Entity<ReinsuranceRecovery>(recovery =>
        {
            recovery.ToTable("recovery");
            recovery.HasKey(r => new { r.TenantId, r.Id });
            recovery.HasAuditColumns();
            recovery.Ignore(r => r.DomainEvents);
            recovery.Ignore(r => r.HasChanges);
            recovery.Ignore(r => r.Difference);
            recovery.Property(r => r.ClaimNumber).HasMaxLength(25);
            recovery.Property(r => r.SourceDomain).HasMaxLength(10);
            recovery.Property(r => r.TreatyCode).HasMaxLength(15);
            recovery.Property(r => r.PaidAmount).HasPrecision(18, 2);
            recovery.Property(r => r.Recoverable).HasPrecision(18, 2);
            recovery.Property(r => r.SettledAmount).HasPrecision(18, 2);
            recovery.Property(r => r.Currency).HasColumnType("char(3)");
            recovery.Property(r => r.Status).HasConversion(new UpperCaseEnumConverter<RecoveryStatus>()).HasMaxLength(10);
        });

        modelBuilder.Entity<Bordereau>(bordereau =>
        {
            bordereau.ToTable("bordereau");
            bordereau.HasKey(b => new { b.TenantId, b.Id });
            bordereau.HasAuditColumns();
            bordereau.Ignore(b => b.DomainEvents);
            bordereau.Ignore(b => b.HasChanges);
            bordereau.Property(b => b.TreatyCode).HasMaxLength(15);
            bordereau.Property(b => b.PeriodCode).HasColumnType("char(7)");
            bordereau.Property(b => b.LayoutCode).HasMaxLength(15);
            bordereau.Property(b => b.Status).HasConversion(new UpperCaseEnumConverter<BordereauStatus>()).HasMaxLength(10);
            bordereau.Property(b => b.PartnerRef).HasMaxLength(80);
        });
    }
}
