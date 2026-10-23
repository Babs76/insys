/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Infrastructure
 * CONFIDENTIALITY AND INTELLECTUAL PROPERTY NOTICE
 * This source code and its associated documentation contain confidential and
 * proprietary information belonging to OpenFactor Technology Group or its
 * applicable intellectual property rights holder.
 * This material is provided solely for authorised development, maintenance,
 * operation or evaluation purposes, as applicable. No person may reproduce,
 * modify, distribute, disclose, publish or otherwise use this material except
 * as expressly permitted by an applicable written agreement or licence.
 * No ownership rights or licence to any intellectual property are granted
 * by the inclusion of this notice, except as expressly provided in an
 * applicable written agreement or licence.
 * Third-party software, open-source components and customer-owned materials
 * remain subject to their respective ownership rights and applicable licences
 * or agreements.
 * UNAUTHORISED ACCESS, USE, COPYING OR DISCLOSURE IS PROHIBITED.
 * ============================================================================
 */
using System.Linq.Expressions;
using System.Text.Json;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Contracts.Auditing;
using Insys.BuildingBlocks.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Insys.BuildingBlocks.Infrastructure.Persistence;

/// <summary>Services every module DbContext needs.</summary>
public sealed class ModuleDbContextDependencies(
    ITenantContext tenant, IExecutionContext execution, IClock clock, IOptions<PlatformOptions> platform, IServiceProvider services)
{
    public ITenantContext Tenant { get; } = tenant;

    public IExecutionContext Execution { get; } = execution;

    public IClock Clock { get; } = clock;

    public PlatformOptions Platform { get; } = platform.Value;

    public IServiceProvider Services { get; } = services;
}

/// <summary>
/// Base for a module's DbContext. It maps only the module's own schemas (DBA-004), adds the tenant query filter as a
/// second layer behind row-level security, maintains row_version and the audit columns, and writes the aggregates'
/// events and audit entries to the module outbox in the same SaveChanges (DBA-006).
/// </summary>
public abstract class ModuleDbContext(DbContextOptions options, ModuleDbContextDependencies dependencies) : DbContext(options)
{
    public const string CreatedAt = "CreatedAt";
    public const string CreatedBy = "CreatedBy";
    public const string UpdatedAt = "UpdatedAt";
    public const string UpdatedBy = "UpdatedBy";
    public const string TenantFilterKey = "tenant";

    public static readonly JsonSerializerOptions EventJson = new(JsonSerializerDefaults.Web);

    private Guid? _actingTenantId;

    /// <summary>The module's primary PostgreSQL schema, for example "integration". Its outbox and inbox live here.</summary>
    public abstract string Schema { get; }

    /// <summary>Every schema the module owns (db/roles/module_roles.csv). Usually just <see cref="Schema"/>.</summary>
    public virtual IReadOnlyCollection<string> OwnedSchemas => [Schema];

    /// <summary>True only for contexts whose operators act on behalf of a tenant (the Tenancy module, TEN-007).</summary>
    protected virtual bool AllowsOperatorActingAsTenant => false;

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    internal IExecutionContext Execution => dependencies.Execution;

    /// <summary>
    /// The tenant of this context's transactions: the caller's tenant, or the target tenant of an operator command.
    /// Referenced by the global query filter; throws when there is no tenant, so a query never runs unscoped.
    /// </summary>
    public Guid CurrentTenantId => _actingTenantId ?? dependencies.Tenant.RequireTenantId();

    /// <summary>
    /// Runs this context's transactions as <paramref name="tenantId"/>. Allowed only in contexts that permit it and
    /// only for callers from the platform operator tenant (TEN-007).
    /// </summary>
    public void ActAsTenant(Guid tenantId)
    {
        if (!AllowsOperatorActingAsTenant || dependencies.Tenant.TenantId != dependencies.Platform.OperatorTenantId
            || dependencies.Platform.OperatorTenantId == Guid.Empty)
        {
            throw new TenantMismatchException();
        }

        if (Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException("Choose the target tenant before the transaction starts.");
        }

        _actingTenantId = tenantId;
    }

    /// <summary>
    /// Runs <paramref name="work"/> as <paramref name="tenantId"/> inside the current transaction, then saves it and
    /// returns to the caller's tenant: for a platform operator's approved change that writes a decision in the operator
    /// tenant and its effect in another tenant atomically (OQ-067). The same rules as <see cref="ActAsTenant"/> apply.
    /// Changes already made are saved first, so each tenant's rows are stamped with their own tenant.
    /// </summary>
    public async Task WithinTenant(Guid tenantId, Func<Task> work, CancellationToken cancellationToken)
    {
        if (!AllowsOperatorActingAsTenant || dependencies.Tenant.TenantId != dependencies.Platform.OperatorTenantId
            || dependencies.Platform.OperatorTenantId == Guid.Empty)
        {
            throw new TenantMismatchException();
        }

        var transaction = Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException("WithinTenant runs inside the caller's transaction.");
        await SaveChangesAsync(cancellationToken);
        var previous = _actingTenantId;
        _actingTenantId = tenantId;
        await TenantSession.SetTenant(Database.GetDbConnection(), transaction, tenantId, cancellationToken);
        try
        {
            await work();
            await SaveChangesAsync(cancellationToken);
        }
        finally
        {
            _actingTenantId = previous;
            await TenantSession.SetTenant(Database.GetDbConnection(), transaction, CurrentTenantId, cancellationToken);
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.Entity<OutboxMessage>(outbox =>
        {
            outbox.ToTable("outbox_event", Schema);
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

    /// <summary>Adds an integration event to this module's outbox; it is written by the next SaveChanges.</summary>
    public void Publish(IIntegrationEvent integrationEvent, string aggregateType, Guid aggregateId, DateTimeOffset occurredAt)
    {
        var descriptor = IntegrationEventTypes.Describe(integrationEvent.GetType());
        OutboxMessages.Add(new OutboxMessage
        {
            TenantId = CurrentTenantId,
            Id = Guid.CreateVersion7(),
            AggregateType = aggregateType.Length <= 60 ? aggregateType : aggregateType[..60],
            AggregateId = aggregateId,
            EventType = descriptor.Type,
            EventVersion = descriptor.Version,
            Payload = JsonSerializer.Serialize(integrationEvent, integrationEvent.GetType(), EventJson),
            OccurredAt = occurredAt,
            CorrelationId = dependencies.Execution.CorrelationId,
            CausationId = dependencies.Execution.CausationId,
        });
    }

    /// <summary>Publishes audit entries as <see cref="SensitiveOperationRecorded"/> events through this module's outbox.</summary>
    /// <summary>The audit event type of the entries that make every change to a record part of its history.</summary>
    public const string RecordChanged = "RECORD_CHANGED";

    /// <summary>
    /// The audit entries a successful unit of work adds for record history (Wave 0): one per aggregate it adds or
    /// changes, named by the aggregate's type and ID, unless the handler's own entries already name that record. So a
    /// record's history (audit events by target type and ID) has every change to it, not only the sensitive ones
    /// (IAM-006). The entries carry no details, so no personal data.
    /// </summary>
    public IReadOnlyList<AuditEntry> RecordChanges(string operation, IReadOnlyCollection<AuditEntry> explicitEntries)
    {
        ChangeTracker.DetectChanges();
        return ChangeTracker.Entries()
            .Where(e => e.Entity is IAggregateRoot aggregate
                && (e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted || aggregate.HasChanges))
            .Select(e => (Type: e.Entity.GetType().Name, Id: e.Metadata.FindProperty("Id") is { } id ? e.Property(id.Name).CurrentValue?.ToString() ?? "" : ""))
            .Where(t => t.Id.Length > 0 && !explicitEntries.Any(x => x.TargetType == t.Type && x.TargetId == t.Id))
            .Distinct()
            .Select(t => new AuditEntry(RecordChanged, operation.Length <= 40 ? operation : operation[..40], t.Type, t.Id))
            .ToList();
    }

    public void PublishAudit(IEnumerable<AuditEntry> entries, AuditOutcome outcome)
    {
        var now = dependencies.Clock.UtcNow;
        var execution = dependencies.Execution;
        foreach (var entry in entries)
        {
            var targetId = Guid.TryParse(entry.TargetId, out var parsed) ? parsed : Guid.Empty;
            Publish(new SensitiveOperationRecorded(execution.ActorId, execution.ActorType == ActorType.Service ? "SERVICE" : "USER",
                entry.EventType, entry.Operation, entry.TargetType, entry.TargetId, outcome.ToString().ToUpperInvariant(), now, entry.Details),
                entry.TargetType, targetId, now);
        }
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

            var tenantProperty = entityType.FindProperty(nameof(ITenantScoped.TenantId));
            if (tenantProperty is not null && entityType.BaseType is null)
            {
                modelBuilder.Entity(entityType.ClrType).HasQueryFilter(TenantFilterKey,
                    BuildTenantFilter(entityType.ClrType, tenantProperty.ClrType == typeof(Guid?)));
            }
        }
    }

    /// <summary>
    /// The second layer behind row-level security: own tenant only, or for static data with a nullable tenant_id,
    /// global rows plus the tenant's own (scopes G and GT).
    /// </summary>
    private LambdaExpression BuildTenantFilter(Type clrType, bool includeGlobalRows)
    {
        var entity = Expression.Parameter(clrType, "e");
        var current = Expression.Property(Expression.Constant(this), nameof(CurrentTenantId));
        if (!includeGlobalRows)
        {
            var tenantId = Expression.Call(typeof(EF), nameof(EF.Property), [typeof(Guid)], entity, Expression.Constant(nameof(ITenantScoped.TenantId)));
            return Expression.Lambda(Expression.Equal(tenantId, current), entity);
        }

        var nullableTenant = Expression.Call(typeof(EF), nameof(EF.Property), [typeof(Guid?)], entity, Expression.Constant(nameof(ITenantScoped.TenantId)));
        return Expression.Lambda(
            Expression.OrElse(
                Expression.Equal(nullableTenant, Expression.Constant(null, typeof(Guid?))),
                Expression.Equal(nullableTenant, Expression.Convert(current, typeof(Guid?)))),
            entity);
    }

    private void WriteOutbox()
    {
        var aggregates = ChangeTracker.Entries<IAggregateRoot>().Select(e => e.Entity).Where(a => a.DomainEvents.Count > 0).ToList();
        if (aggregates.Count == 0)
        {
            return;
        }

        var mappers = dependencies.Services.GetKeyedServices<IDomainEventMapper>(Schema).ToList();
        foreach (var aggregate in aggregates)
        {
            var aggregateId = aggregate.Id as Guid? ?? throw new InvalidOperationException("Aggregate IDs must be GUIDs to be referenced from events.");
            foreach (var domainEvent in aggregate.DequeueDomainEvents())
            {
                var integrationEvent = mappers.Select(m => m.Map(domainEvent)).FirstOrDefault(e => e is not null)
                    ?? throw new InvalidOperationException($"No IDomainEventMapper in the {Schema} module maps {domainEvent.GetType().Name}.");
                Publish(integrationEvent, aggregate.GetType().Name, aggregateId, domainEvent.OccurredAt);
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

    /// <summary>Sets tenant_id on new rows. Nullable tenant columns (static data) are set by the static data store.</summary>
    private static void StampTenant(EntityEntry entry, Guid tenantId)
    {
        var metadata = entry.Metadata.FindProperty(nameof(ITenantScoped.TenantId));
        if (metadata is null || metadata.ClrType != typeof(Guid))
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
        var metadata = entry.Metadata.FindProperty(nameof(ITenantScoped.TenantId));
        if (metadata is null)
        {
            return;
        }

        var property = entry.Property(nameof(ITenantScoped.TenantId));
        if (metadata.ClrType == typeof(Guid?) && property.OriginalValue is null && property.CurrentValue is null)
        {
            // A global static data row; row-level security allows the write only to platform administrators.
            return;
        }

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
