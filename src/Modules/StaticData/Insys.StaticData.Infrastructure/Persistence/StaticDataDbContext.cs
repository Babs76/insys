using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.StaticData.Infrastructure.StaticData;
using Insys.StaticData.Domain.Changes;
using Microsoft.EntityFrameworkCore;

namespace Insys.StaticData.Infrastructure.Persistence;

/// <summary>The StaticData module's context: its own schema only, on its own database role (DBA-004).</summary>
public sealed class StaticDataDbContext(DbContextOptions<StaticDataDbContext> options, ModuleDbContextDependencies dependencies)
    : ModuleDbContext(options, dependencies)
{
    public const string SchemaName = "sdm";

    public override string Schema => SchemaName;

    /// <summary>Static data governance (sdm), shared reference data (refdata) and platform operations configuration (ops), which have no module of their own (ADR-002).</summary>
    public override IReadOnlyCollection<string> OwnedSchemas => [SchemaName, "refdata", "ops"];

    public DbSet<StaticDataChange> Changes => Set<StaticDataChange>();

    public DbSet<ImportBatch> ImportBatches => Set<ImportBatch>();

    public DbSet<TenantGovernanceSettings> Settings => Set<TenantGovernanceSettings>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        StaticDataStaticData.Configure(modelBuilder);

        modelBuilder.Entity<StaticDataChange>(change =>
        {
            change.ToTable("change_request");
            change.HasKey(c => new { c.TenantId, c.Id });
            change.HasAuditColumns();
            change.Ignore(c => c.DomainEvents);
            change.Ignore(c => c.HasChanges);
            change.Property(c => c.RequestNumber).HasMaxLength(20);
            change.Property(c => c.EntityCode).HasMaxLength(40);
            change.Property(c => c.RecordKey).HasMaxLength(120);
            change.Property(c => c.Action).HasMaxLength(10);
            change.Property(c => c.Before).HasColumnType("jsonb");
            change.Property(c => c.After).HasColumnType("jsonb");
            change.Property(c => c.Status).HasConversion(new UpperCaseEnumConverter<ChangeStatus>()).HasMaxLength(15);
        });

        modelBuilder.Entity<ImportBatch>(batch =>
        {
            batch.ToTable("import_batch");
            batch.HasKey(b => new { b.TenantId, b.Id });
            batch.HasAuditColumns();
            batch.Ignore(b => b.DomainEvents);
            batch.Ignore(b => b.HasChanges);
            batch.Property(b => b.BatchNumber).HasMaxLength(20);
            batch.Property(b => b.EntityCode).HasMaxLength(40);
            batch.Property(b => b.FileRef).HasMaxLength(200);
            batch.Property(b => b.Status).HasConversion(new UpperCaseEnumConverter<ImportStatus>()).HasMaxLength(12);
            batch.HasMany(b => b.Errors).WithOne().HasForeignKey(e => new { e.TenantId, e.BatchId });
            batch.Navigation(b => b.Errors).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<TenantGovernanceSettings>(settings =>
        {
            settings.ToTable("tenant_governance_setting");
            settings.HasKey(x => new { x.TenantId, x.Id });
            settings.HasAuditColumns();
            settings.Ignore(x => x.DomainEvents);
            settings.Ignore(x => x.HasChanges);
            settings.Property(x => x.Tier2MakerChecker).HasColumnName("tier2_maker_checker");
            settings.Property(x => x.PendingTier2MakerChecker).HasColumnName("pending_tier2_maker_checker");
            settings.Property(x => x.PendingRequestedBy).HasColumnName("pending_requested_by");
            settings.Property(x => x.PendingRequestedAt).HasColumnName("pending_requested_at");
        });

        modelBuilder.Entity<ImportError>(error =>
        {
            error.ToTable("import_error");
            error.HasKey(e => new { e.TenantId, e.Id });
            error.HasAuditColumns(updatable: false);
            error.Property(e => e.Field).HasMaxLength(60);
        });
    }
}
