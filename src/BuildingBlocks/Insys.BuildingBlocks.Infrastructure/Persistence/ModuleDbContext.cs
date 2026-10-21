using System.Linq.Expressions;
using System.Text.Json;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.BuildingBlocks.Infrastructure.Persistence;

/// <summary>Services every module DbContext needs.</summary>
public sealed class ModuleDbContextDependencies(ITenantContext tenant, IExecutionContext execution, IClock clock, IServiceProvider services)
{
    public ITenantContext Tenant { get; } = tenant;

    public IExecutionContext Execution { get; } = execution;

    public IClock Clock { get; } = clock;

    public IServiceProvider Services { get; } = services;
}

/// <summary>
/// Base for a module's DbContext. It maps only the module's own schema (DBA-004), adds the tenant query filter as a
/// second layer behind row-level security, maintains row_version and the audit columns, and writes the aggregates'
/// events to the module outbox in the same SaveChanges (DBA-006).
/// </summary>
public abstract class ModuleDbContext(DbContextOptions options, ModuleDbContextDependencies dependencies) : DbContext(options)
{
    public const string CreatedAt = "CreatedAt";
    public const string CreatedBy = "CreatedBy";
    public const string UpdatedAt = "UpdatedAt";
    public const string UpdatedBy = "UpdatedBy";

    public const string TenantFilterKey = "tenant";

    public static readonly JsonSerializerOptions EventJson = new(JsonSerializerDefaults.Web);

    /// <summary>The module's PostgreSQL schema, for example "integration".</summary>
    public abstract string Schema { get; }

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    internal ITenantContext TenantContext => dependencies.Tenant;

    /// <summary>Referenced by the global query filter; throws when there is no tenant, so a query never runs unscoped.</summary>
    protected Guid CurrentTenantId => dependencies.Tenant.RequireTenantId();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.Entity<OutboxMessage>(outbox =>
        {
            outbox.ToTable("outbox_event");
            outbox.HasKey(o => new { o.TenantId, o.Id });
            outbox.Property(o => o.Payload).HasColumnType("jsonb");
            outbox.Property(o => o.RowVersion).IsConcurrencyToken();
            outbox.HasAuditColumns();
        });

        ConfigureModel(modelBuilder);
        ApplyConventions(modelBuilder);
    }

    protected abstract void ConfigureModel(ModelBuilder modelBuilder);

    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        throw new NotSupportedException("Use SaveChangesAsync.");

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        WriteOutbox();
        StampRows();
        var count = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        foreach (var entry in ChangeTracker.Entries<IAggregateRoot>())
        {
            entry.Entity.AcceptChanges();
        }

        return count;
    }

    private void ApplyConventions(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes().ToList())
        {
            if (entityType.IsOwned())
            {
                continue;
            }

            foreach (var property in entityType.GetProperties())
            {
                if (property.GetColumnName() == property.Name)
                {
                    property.SetColumnName(SnakeCase.From(property.Name));
                }

                if (property.Name == nameof(IAggregateRoot.RowVersion))
                {
                    property.IsConcurrencyToken = true;
                }
            }

            if (entityType.FindProperty(nameof(ITenantScoped.TenantId)) is not null && entityType.BaseType is null)
            {
                modelBuilder.Entity(entityType.ClrType).HasQueryFilter(TenantFilterKey, BuildTenantFilter(entityType.ClrType));
            }
        }
    }

    private LambdaExpression BuildTenantFilter(Type clrType)
    {
        var entity = Expression.Parameter(clrType, "e");
        var tenantId = Expression.Call(typeof(EF), nameof(EF.Property), [typeof(Guid)], entity, Expression.Constant(nameof(ITenantScoped.TenantId)));
        var current = Expression.Property(Expression.Constant(this), nameof(CurrentTenantId));
        return Expression.Lambda(Expression.Equal(tenantId, current), entity);
    }

    private void WriteOutbox()
    {
        var aggregates = ChangeTracker.Entries<IAggregateRoot>().Select(e => e.Entity).Where(a => a.DomainEvents.Count > 0).ToList();
        if (aggregates.Count == 0)
        {
            return;
        }

        var mappers = dependencies.Services.GetKeyedServices<IDomainEventMapper>(Schema).ToList();
        var tenantId = CurrentTenantId;
        foreach (var aggregate in aggregates)
        {
            var aggregateId = aggregate.Id as Guid? ?? throw new InvalidOperationException("Aggregate IDs must be GUIDs to be referenced from events.");
            foreach (var domainEvent in aggregate.DequeueDomainEvents())
            {
                var integrationEvent = mappers.Select(m => m.Map(domainEvent)).FirstOrDefault(e => e is not null)
                    ?? throw new InvalidOperationException($"No IDomainEventMapper in the {Schema} module maps {domainEvent.GetType().Name}.");
                var descriptor = IntegrationEventTypes.Describe(integrationEvent.GetType());
                OutboxMessages.Add(new OutboxMessage
                {
                    TenantId = tenantId,
                    Id = Guid.CreateVersion7(),
                    AggregateType = aggregate.GetType().Name,
                    AggregateId = aggregateId,
                    EventType = descriptor.Type,
                    EventVersion = descriptor.Version,
                    Payload = JsonSerializer.Serialize(integrationEvent, integrationEvent.GetType(), EventJson),
                    OccurredAt = domainEvent.OccurredAt,
                    CorrelationId = dependencies.Execution.CorrelationId,
                    CausationId = dependencies.Execution.CausationId,
                });
            }
        }
    }

    private void StampRows()
    {
        var tenantId = CurrentTenantId;
        var now = dependencies.Clock.UtcNow;
        var actor = dependencies.Execution.ActorId;

        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State == EntityState.Unchanged && entry.Entity is IAggregateRoot { HasChanges: true })
            {
                entry.State = EntityState.Modified;
            }

            switch (entry.State)
            {
                case EntityState.Added:
                    StampTenant(entry, tenantId);
                    SetIfPresent(entry, nameof(IAggregateRoot.RowVersion), 1L);
                    SetIfPresent(entry, CreatedAt, now);
                    SetIfPresent(entry, CreatedBy, actor);
                    SetIfPresent(entry, UpdatedAt, now);
                    SetIfPresent(entry, UpdatedBy, actor);
                    break;
                case EntityState.Modified:
                    EnsureSameTenant(entry, tenantId);
                    if (entry.Metadata.FindProperty(nameof(IAggregateRoot.RowVersion)) is not null)
                    {
                        var version = entry.Property(nameof(IAggregateRoot.RowVersion));
                        version.CurrentValue = (long)version.OriginalValue! + 1;
                    }

                    SetIfPresent(entry, UpdatedAt, now);
                    SetIfPresent(entry, UpdatedBy, actor);
                    break;
                case EntityState.Deleted:
                    EnsureSameTenant(entry, tenantId);
                    break;
            }
        }
    }

    private static void StampTenant(EntityEntry entry, Guid tenantId)
    {
        if (entry.Metadata.FindProperty(nameof(ITenantScoped.TenantId)) is null)
        {
            return;
        }

        var property = entry.Property(nameof(ITenantScoped.TenantId));
        if (property.CurrentValue is Guid existing && existing != Guid.Empty && existing != tenantId)
        {
            throw new TenantMismatchException();
        }

        property.CurrentValue = tenantId;
    }

    private static void EnsureSameTenant(EntityEntry entry, Guid tenantId)
    {
        if (entry.Metadata.FindProperty(nameof(ITenantScoped.TenantId)) is null)
        {
            return;
        }

        var property = entry.Property(nameof(ITenantScoped.TenantId));
        if (!Equals(property.OriginalValue, tenantId) || !Equals(property.CurrentValue, tenantId))
        {
            throw new TenantMismatchException();
        }
    }

    private static void SetIfPresent(EntityEntry entry, string propertyName, object value)
    {
        if (entry.Metadata.FindProperty(propertyName) is not null)
        {
            entry.Property(propertyName).CurrentValue = value;
        }
    }
}

/// <summary>A row for one tenant was about to be written while another tenant's context was active.</summary>
public sealed class TenantMismatchException() : InvalidOperationException("The row belongs to a different tenant from the current context.");

public static class EntityTypeBuilderExtensions
{
    /// <summary>Maps created_at, created_by, updated_at and updated_by as shadow properties set by the DbContext.</summary>
    public static Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder HasAuditColumns(
        this Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder builder, bool updatable = true)
    {
        builder.Property<DateTimeOffset>(ModuleDbContext.CreatedAt);
        builder.Property<string>(ModuleDbContext.CreatedBy).HasMaxLength(80);
        if (updatable)
        {
            builder.Property<DateTimeOffset>(ModuleDbContext.UpdatedAt);
            builder.Property<string>(ModuleDbContext.UpdatedBy).HasMaxLength(80);
        }

        return builder;
    }

    public static Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T> HasAuditColumns<T>(
        this Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T> builder, bool updatable = true)
        where T : class
    {
        ((Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder)builder).HasAuditColumns(updatable);
        return builder;
    }
}
