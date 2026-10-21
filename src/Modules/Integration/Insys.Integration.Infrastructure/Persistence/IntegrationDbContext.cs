using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.Integration.Domain.DeadLetters;
using Insys.Integration.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;

namespace Insys.Integration.Infrastructure.Persistence;

/// <summary>
/// The Integration module's context. Maps only the integration schema. The platform tables in this schema used by
/// every module (idempotency_key and inbox_message) are written by the building blocks with SQL, not mapped here.
/// </summary>
public sealed class IntegrationDbContext(DbContextOptions<IntegrationDbContext> options, ModuleDbContextDependencies dependencies)
    : ModuleDbContext(options, dependencies)
{
    public const string SchemaName = "integration";

    public override string Schema => SchemaName;

    /// <summary>The integration schema and the portal and partner channel configuration (channel), which has no module of its own (ADR-002).</summary>
    public override IReadOnlyCollection<string> OwnedSchemas => [SchemaName, "channel"];

    public DbSet<DeadLetter> DeadLetters => Set<DeadLetter>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        IntegrationStaticData.Configure(modelBuilder);
        modelBuilder.Entity<DeadLetter>(entity =>
        {
            entity.ToTable("dead_letter_message");
            entity.HasKey(d => new { d.TenantId, d.Id });
            entity.HasAuditColumns();
            entity.Ignore(d => d.DomainEvents);
            entity.Ignore(d => d.HasChanges);
            entity.Property(d => d.Source).HasColumnName("source").HasMaxLength(60)
                .HasConversion(v => v.Value, v => new Source(v));
            entity.Property(d => d.MessageRef).HasColumnName("message_id").HasMaxLength(80)
                .HasConversion(v => v.Value, v => new MessageRef(v));
            entity.Property(d => d.Payload).HasColumnType("jsonb");
            entity.Property(d => d.Error).HasColumnName("error")
                .HasConversion(v => v.Value, v => new DeliveryError(v));
            entity.Ignore(d => d.Status);

            // Stored as upper-case codes (the schema's enum convention); NULL while dead-lettered.
            entity.Property(d => d.Resolution).HasColumnName("resolution").HasMaxLength(20)
                .HasConversion(v => v == DeadLetterResolution.Replayed ? "REPLAYED" : "DISCARDED",
                               v => v == "REPLAYED" ? DeadLetterResolution.Replayed : DeadLetterResolution.Discarded);
        });
    }
}
