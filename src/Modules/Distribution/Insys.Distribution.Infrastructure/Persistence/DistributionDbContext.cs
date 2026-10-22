using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.Distribution.Domain.Commission;
using Insys.Distribution.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;

namespace Insys.Distribution.Infrastructure.Persistence;

/// <summary>The Distribution module's context: its own schema only, on its own database role (DBA-004).</summary>
public sealed class DistributionDbContext(DbContextOptions<DistributionDbContext> options, ModuleDbContextDependencies dependencies)
    : ModuleDbContext(options, dependencies)
{
    public const string SchemaName = "commission";

    public override string Schema => SchemaName;

    public DbSet<CommissionEntitlement> Entitlements => Set<CommissionEntitlement>();

    public DbSet<CommissionStatement> Statements => Set<CommissionStatement>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        DistributionStaticData.Configure(modelBuilder);

        modelBuilder.Entity<CommissionEntitlement>(entitlement =>
        {
            entitlement.ToTable("commission_entitlement");
            entitlement.HasKey(e => new { e.TenantId, e.Id });
            entitlement.HasAuditColumns();
            entitlement.Ignore(e => e.DomainEvents);
            entitlement.Ignore(e => e.HasChanges);
            entitlement.Ignore(e => e.Clawback);
            entitlement.Ignore(e => e.NeedsRecovery);
            entitlement.Property(e => e.IntermediaryCode).HasMaxLength(15);
            entitlement.Property(e => e.SourceType).HasMaxLength(15);
            entitlement.Property(e => e.SourceDomain).HasMaxLength(10);
            entitlement.Property(e => e.ProductCode).HasMaxLength(15);
            entitlement.Property(e => e.PlanCode).HasMaxLength(15);
            entitlement.Property(e => e.BasisAmount).HasPrecision(18, 2);
            entitlement.Property(e => e.Rate).HasPrecision(7, 4);
            entitlement.Property(e => e.Amount).HasPrecision(18, 2);
            entitlement.Property(e => e.Currency).HasColumnType("char(3)");
            entitlement.Property(e => e.Status).HasConversion(new UpperCaseEnumConverter<EntitlementStatus>()).HasMaxLength(12);
            entitlement.HasMany(e => e.Clawbacks).WithOne().HasForeignKey(c => new { c.TenantId, c.EntitlementId });

            // The statement foreign keys (V0001, V0021), mapped so a new statement is inserted before the lines that reference it.
            entitlement.HasOne<CommissionStatement>().WithMany().HasForeignKey(e => new { e.TenantId, e.StatementId });
            entitlement.Navigation(e => e.Clawbacks).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<Clawback>(clawback =>
        {
            clawback.ToTable("clawback");
            clawback.HasKey(c => new { c.TenantId, c.Id });
            clawback.HasAuditColumns(updatable: false);
            clawback.Property(c => c.Reason).HasMaxLength(20);
            clawback.Property(c => c.Amount).HasPrecision(18, 2);
            clawback.HasOne<CommissionStatement>().WithMany().HasForeignKey(c => new { c.TenantId, c.StatementId });
        });

        modelBuilder.Entity<CommissionStatement>(statement =>
        {
            statement.ToTable("commission_statement");
            statement.HasKey(s => new { s.TenantId, s.Id });
            statement.HasAuditColumns();
            statement.Ignore(s => s.DomainEvents);
            statement.Ignore(s => s.HasChanges);
            statement.Ignore(s => s.ApprovalPending);
            statement.Ignore(s => s.SubjectHash);
            statement.Property(s => s.IntermediaryCode).HasMaxLength(15);
            statement.Property(s => s.PeriodCode).HasColumnType("char(7)");
            statement.Property(s => s.Currency).HasColumnType("char(3)");
            statement.Property(s => s.GrossAmount).HasPrecision(18, 2);
            statement.Property(s => s.TaxAmount).HasPrecision(18, 2);
            statement.Property(s => s.NetAmount).HasPrecision(18, 2);
            statement.Property(s => s.Status).HasConversion(new UpperCaseEnumConverter<StatementStatus>()).HasMaxLength(10);
            statement.Property(s => s.BankAccountHash).HasMaxLength(64);
        });
    }
}
