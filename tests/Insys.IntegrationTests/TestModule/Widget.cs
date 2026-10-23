/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : IntegrationTests
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

public sealed record WidgetRenamedDomainEvent(Guid WidgetId, string Name, DateTimeOffset OccurredAt) : IDomainEvent;

[IntegrationEvent("test_mod.WidgetRenamed")]
public sealed record WidgetRenamed(Guid WidgetId, string Name) : IIntegrationEvent;

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

    public void Rename(string name, DateTimeOffset now)
    {
        Name = name;
        Raise(new WidgetRenamedDomainEvent(Id, name, now));
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
        WidgetRenamedDomainEvent renamed => new WidgetRenamed(renamed.WidgetId, renamed.Name),
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

public sealed class RenameWidgetHandler(TestDbContext db, IClock clock) : ICommandHandler<RenameWidget, long>
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

        widget.Rename(command.Name, clock.UtcNow);
        return widget.RowVersion + 1;
    }
}

/// <summary>Controls the consumers from tests.</summary>
public sealed class ConsumerSwitch
{
    public volatile bool Fail;

    /// <summary>Widgets whose configuration the ledger consumer finds missing: it parks their events (ADR-022).</summary>
    public readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, bool> Missing = new();

    /// <summary>Widgets whose events the ledger consumer fails as a fault, not as missing configuration.</summary>
    public readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, bool> Failing = new();

    /// <summary>Widgets whose events the ledger consumer handles too slowly for the attempt timeout.</summary>
    public readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, bool> Slow = new();
}

/// <summary>
/// A second consumer of the widget events, for the delivery ledger (ADR-022): it records each event it applies, in
/// order, in test_mod.ledger_consumption, and parks, fails or stalls on the widgets the switch names.
/// </summary>
public sealed class WidgetLedgerConsumer(TestDbContext db, ConsumerSwitch consumerSwitch) : IIntegrationEventHandler<WidgetCreated>, IIntegrationEventHandler<WidgetRenamed>
{
    public const string MissingCode = "WIDGET_COLOUR_NOT_IN_FORCE";

    public Task Handle(WidgetCreated integrationEvent, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Apply(integrationEvent.WidgetId, "created", envelope, cancellationToken);

    public Task Handle(WidgetRenamed integrationEvent, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Apply(integrationEvent.WidgetId, integrationEvent.Name, envelope, cancellationToken);

    private async Task Apply(Guid widgetId, string what, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (consumerSwitch.Missing.ContainsKey(widgetId))
        {
            throw new PrerequisiteMissingException(MissingCode, "BLUE");
        }

        if (consumerSwitch.Failing.ContainsKey(widgetId))
        {
            throw new InvalidOperationException("Ledger consumer told to fail.");
        }

        if (consumerSwitch.Slow.ContainsKey(widgetId))
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
        }

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO test_mod.ledger_consumption (tenant_id, event_id, widget_id, what) VALUES ({envelope.TenantId}, {envelope.EventId}, {widgetId}, {what})",
            cancellationToken);
    }
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
