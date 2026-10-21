using FluentValidation;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Insys.IntegrationTests.TestModule;

// A test-only module in schema test_mod, created by TestModuleSchema. It exercises the building blocks against
// real PostgreSQL without depending on any production module's rules.

public sealed record WidgetCreatedDomainEvent(Guid WidgetId, string Name, DateTimeOffset OccurredAt) : IDomainEvent;

[IntegrationEvent("test_mod.WidgetCreated")]
public sealed record WidgetCreated(Guid WidgetId) : IIntegrationEvent;

public sealed class Widget : AggregateRoot<Guid>
{
    private Widget()
    {
    }

    public string Name { get; private set; } = "";

    public static Widget Create(Guid id, string name, DateTimeOffset now)
    {
        var widget = new Widget { Id = id, Name = name };
        widget.Raise(new WidgetCreatedDomainEvent(id, name, now));
        return widget;
    }

    public void Rename(string name)
    {
        Name = name;
        MarkChanged();
    }
}

/// <summary>A side effect written by the consumer, so tests can count deliveries.</summary>
public sealed class Consumption
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid EventId { get; set; }

    public Guid WidgetId { get; set; }

    public string Actor { get; set; } = "";
}

public sealed class TestDbContext(DbContextOptions<TestDbContext> options, ModuleDbContextDependencies dependencies) : ModuleDbContext(options, dependencies)
{
    public const string SchemaName = "test_mod";

    public override string Schema => SchemaName;

    public DbSet<Widget> Widgets => Set<Widget>();

    public DbSet<Consumption> Consumptions => Set<Consumption>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Widget>(entity =>
        {
            entity.ToTable("widget");
            entity.HasKey(w => new { w.TenantId, w.Id });
            entity.HasAuditColumns();
            entity.Ignore(w => w.DomainEvents);
            entity.Ignore(w => w.HasChanges);
        });
        modelBuilder.Entity<Consumption>(entity =>
        {
            entity.ToTable("consumption");
            entity.HasKey(c => new { c.TenantId, c.Id });
        });
    }
}

public sealed class WidgetEventMapper : IDomainEventMapper
{
    public IIntegrationEvent? Map(IDomainEvent domainEvent) => domainEvent switch
    {
        WidgetCreatedDomainEvent created => new WidgetCreated(created.WidgetId),
        _ => null,
    };
}

public sealed record CreateWidget(string Name, bool FailAfterChange = false) : ICommand<Guid>;

public sealed record RenameWidget(Guid WidgetId, long ExpectedVersion, string Name) : ICommand<long>, IVersionedCommand;

public sealed class CreateWidgetValidator : AbstractValidator<CreateWidget>
{
    public CreateWidgetValidator() => RuleFor(c => c.Name).NotEmpty().MaximumLength(40);
}

public sealed class CreateWidgetHandler(TestDbContext db, IClock clock, IAuditTrail audit) : ICommandHandler<CreateWidget, Guid>
{
    public Task<Result<Guid>> Handle(CreateWidget command, CancellationToken cancellationToken)
    {
        var widget = Widget.Create(Guid.CreateVersion7(), command.Name, clock.UtcNow);
        db.Widgets.Add(widget);
        audit.Record(new AuditEntry("WIDGET", "CreateWidget", nameof(Widget), widget.Id.ToString(),
            new Dictionary<string, string?> { ["reference"] = AuditMask.Last4("ACC-0000123456") }));
        return Task.FromResult(command.FailAfterChange
            ? Result<Guid>.Failure(Error.Conflict("TEST_FAILURE", "Fails after changing the aggregate."))
            : Result<Guid>.Success(widget.Id));
    }
}

public sealed class RenameWidgetHandler(TestDbContext db) : ICommandHandler<RenameWidget, long>
{
    public async Task<Result<long>> Handle(RenameWidget command, CancellationToken cancellationToken)
    {
        var widget = await db.Widgets.SingleOrDefaultAsync(w => w.Id == command.WidgetId, cancellationToken);
        if (widget is null)
        {
            return Error.NotFound(ErrorCodes.NotFound, "No widget.");
        }

        if (widget.RowVersion != command.ExpectedVersion)
        {
            return Error.PreconditionFailed(ErrorCodes.PreconditionFailed, "Changed.");
        }

        widget.Rename(command.Name);
        return widget.RowVersion + 1;
    }
}

/// <summary>Controls the consumer from tests.</summary>
public sealed class ConsumerSwitch
{
    public volatile bool Fail;
}

public sealed class WidgetCreatedConsumer(TestDbContext db, IExecutionContext execution, ConsumerSwitch consumerSwitch) : IIntegrationEventHandler<WidgetCreated>
{
    public Task Handle(WidgetCreated integrationEvent, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (consumerSwitch.Fail)
        {
            throw new InvalidOperationException("Consumer told to fail.");
        }

        db.Consumptions.Add(new Consumption
        {
            Id = Guid.CreateVersion7(),
            EventId = envelope.EventId,
            WidgetId = integrationEvent.WidgetId,
            Actor = execution.ActorId,
        });
        return Task.CompletedTask;
    }
}
