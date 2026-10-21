using System.Text.Json;
using Insys.Audit.Infrastructure.StaticData;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Insys.Audit.Infrastructure.Persistence;

/// <summary>A row in audit.audit_event: insert-only and partitioned by month (DBA-016).</summary>
public sealed class AuditEvent
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public string CreatedBy { get; set; } = "";

    public string EventType { get; set; } = "";

    public string ActorType { get; set; } = "";

    public string ActorId { get; set; } = "";

    public string Operation { get; set; } = "";

    public string TargetType { get; set; } = "";

    public string TargetId { get; set; } = "";

    public string Outcome { get; set; } = "";

    public DateTimeOffset OccurredAt { get; set; }

    public Guid CorrelationId { get; set; }

    public JsonElement? Details { get; set; }
}

/// <summary>The Audit module's context: the audit schema only, on the insys_audit role (insert and select only).</summary>
public sealed class AuditDbContext(DbContextOptions<AuditDbContext> options, ModuleDbContextDependencies dependencies) : ModuleDbContext(options, dependencies)
{
    public const string SchemaName = "audit";

    public override string Schema => SchemaName;

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        AuditStaticData.Configure(modelBuilder);
        modelBuilder.Entity<AuditEvent>(entity =>
        {
            entity.ToTable("audit_event");
            entity.HasKey(e => new { e.TenantId, e.Id, e.OccurredAt });
            entity.Property(e => e.CreatedBy).HasMaxLength(80);
            entity.Property(e => e.EventType).HasMaxLength(40);
            entity.Property(e => e.ActorType).HasMaxLength(10);
            entity.Property(e => e.ActorId).HasMaxLength(80);
            entity.Property(e => e.Operation).HasMaxLength(40);
            entity.Property(e => e.TargetType).HasMaxLength(30);
            entity.Property(e => e.TargetId).HasMaxLength(80);
            entity.Property(e => e.Outcome).HasMaxLength(10);
            entity.Property(e => e.Details).HasColumnType("jsonb");
        });
    }
}
