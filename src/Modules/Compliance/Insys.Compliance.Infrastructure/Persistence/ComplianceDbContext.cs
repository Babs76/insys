using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.Compliance.Domain;
using Insys.Compliance.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;

namespace Insys.Compliance.Infrastructure.Persistence;

/// <summary>The Compliance module's context: its own schema only, on its own database role (DBA-004).</summary>
public sealed class ComplianceDbContext(DbContextOptions<ComplianceDbContext> options, ModuleDbContextDependencies dependencies)
    : ModuleDbContext(options, dependencies)
{
    public const string SchemaName = "reporting";

    public override string Schema => SchemaName;

    public DbSet<RegulatoryReturn> Returns => Set<RegulatoryReturn>();

    public DbSet<ReportRun> Runs => Set<ReportRun>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        ComplianceStaticData.Configure(modelBuilder);

        modelBuilder.Entity<RegulatoryReturn>(regulatoryReturn =>
        {
            regulatoryReturn.ToTable("regulatory_return_run");
            regulatoryReturn.HasKey(r => new { r.TenantId, r.Id });
            regulatoryReturn.HasAuditColumns();
            regulatoryReturn.Ignore(r => r.DomainEvents);
            regulatoryReturn.Ignore(r => r.HasChanges);
            regulatoryReturn.Property(r => r.ReturnCode).HasMaxLength(20);
            regulatoryReturn.Property(r => r.PeriodCode).HasColumnType("char(7)");
            regulatoryReturn.Property(r => r.Status).HasConversion(new UpperCaseEnumConverter<ReturnStatus>()).HasMaxLength(12);
            regulatoryReturn.Property(r => r.FieldValues).HasColumnType("jsonb");
            regulatoryReturn.Property(r => r.RegulatorRef).HasMaxLength(80);
        });

        // Insert-only: a run is recorded once.
        modelBuilder.Entity<ReportRun>(run =>
        {
            run.ToTable("report_run");
            run.HasKey(r => new { r.TenantId, r.Id });
            run.HasAuditColumns(updatable: false);
            run.Ignore(r => r.DomainEvents);
            run.Ignore(r => r.HasChanges);
            run.Ignore(r => r.RowVersion);
            run.Property(r => r.ReportCode).HasMaxLength(20);
            run.Property(r => r.Parameters).HasColumnType("jsonb");
            run.Property(r => r.Result).HasColumnType("jsonb");
            run.Property(r => r.Status).HasMaxLength(10);
        });
    }
}
