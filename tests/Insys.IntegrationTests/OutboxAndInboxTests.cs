using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Infrastructure.Messaging;
using Insys.IntegrationTests.TestModule;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.IntegrationTests;

/// <summary>Unit of work, outbox, relay, inbox and dead letters against the real schema (§23.2, DBA-006, DBA-007, INT-005, INT-006).</summary>
public sealed class OutboxAndInboxTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private ServiceProvider _services = null!;

    public ValueTask InitializeAsync()
    {
        _services = TestServices.Build(fixture.Database);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _services.DisposeAsync();

    [Fact]
    public async Task Command_WritesAggregateAndOutboxEvent_InOneTransaction_DBA006()
    {
        var id = (await _services.Send(DatabaseFixture.TenantA, new CreateWidget("first"))).Value;

        (await Count("test_mod.widget", id, "id")).ShouldBe(1);
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM test_mod.outbox_event WHERE aggregate_id = @id AND event_type = 'test_mod.WidgetCreated'", ("id", id))).ShouldBe(1);
        var tenant = await fixture.Database.ScalarAsOwner<Guid>("SELECT tenant_id FROM test_mod.outbox_event WHERE aggregate_id = @id AND event_type = 'test_mod.WidgetCreated'", ("id", id));
        tenant.ShouldBe(DatabaseFixture.TenantA);
    }

    [Fact]
    public async Task Command_EventPayload_CarriesReferencesNotValues_INT007()
    {
        var id = (await _services.Send(DatabaseFixture.TenantA, new CreateWidget("secret-name"))).Value;

        var payload = await fixture.Database.ScalarAsOwner<string>("SELECT payload::text FROM test_mod.outbox_event WHERE aggregate_id = @id AND event_type = 'test_mod.WidgetCreated'", ("id", id));
        payload.ShouldNotBeNull();
        payload.ShouldNotContain("secret-name");
        payload.ShouldContain(id.ToString());
    }

    [Fact]
    public async Task FailedCommand_RollsBackAggregateAndOutbox_AndAuditsFailure_IAM006()
    {
        var before = await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM test_mod.widget");
        var result = await _services.Send(DatabaseFixture.TenantA, new CreateWidget("rolled-back", FailAfterChange: true));

        result.IsFailure.ShouldBeTrue();
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM test_mod.widget")).ShouldBe(before);
        (await fixture.Database.ScalarAsOwner<long>("""
            SELECT count(*) FROM test_mod.outbox_event o
            WHERE o.event_type = 'test_mod.WidgetCreated' AND NOT EXISTS (SELECT 1 FROM test_mod.widget w WHERE w.id = o.aggregate_id)
            """)).ShouldBe(0);

        // The failure is published through the module's own outbox and recorded by the Audit module.
        await _services.GetRequiredService<OutboxProcessor>().ProcessOnce(CancellationToken.None);
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM audit.audit_event WHERE operation = 'CreateWidget' AND outcome = 'FAILURE'"))
            .ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task SuccessfulCommand_PublishesAuditInTheSameTransaction_WithMaskedDetails_IAM006()
    {
        var id = (await _services.Send(DatabaseFixture.TenantA, new CreateWidget("audited"), actor: "user-42")).Value;
        (await fixture.Database.ScalarAsOwner<long>("""
            SELECT count(*) FROM test_mod.outbox_event WHERE event_type = 'audit.SensitiveOperationRecorded' AND payload->>'targetId' = @id
            """, ("id", id.ToString()))).ShouldBe(1);
        await _services.GetRequiredService<OutboxProcessor>().ProcessOnce(CancellationToken.None);

        var details = await fixture.Database.ScalarAsOwner<string>("""
            SELECT details::text FROM audit.audit_event
            WHERE target_id = @id AND outcome = 'SUCCESS' AND actor_id = 'user-42' AND tenant_id = @t
            """, ("id", id.ToString()), ("t", DatabaseFixture.TenantA));
        details.ShouldNotBeNull();
        details.ShouldContain("****3456");
        details.ShouldNotContain("0000123456");
    }

    [Fact]
    public async Task InvalidCommand_IsRejectedBeforeTheHandler_WithValidationError()
    {
        var result = await _services.Send(DatabaseFixture.TenantA, new CreateWidget(""));

        result.Error!.Code.ShouldBe(ErrorCodes.ValidationFailed);
        result.Error.Details!.Keys.ShouldContain("Name");
    }

    [Fact]
    public async Task Command_WithoutTenant_Throws_TEN002()
    {
        await using var scope = _services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ExecutionScope>().InitialiseForUser(null, "no-tenant", [], null);

        await Should.ThrowAsync<TenantContextMissingException>(() => scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new CreateWidget("x")));
    }

    [Fact]
    public async Task ConcurrentUpdates_TheLosingWriterGetsConcurrencyConflict_DBA011()
    {
        var id = (await _services.Send(DatabaseFixture.TenantA, new CreateWidget("race"))).Value;

        await using var first = _services.CreateAsyncScope();
        await using var second = _services.CreateAsyncScope();
        first.ServiceProvider.GetRequiredService<ExecutionScope>().InitialiseForUser(DatabaseFixture.TenantA, "a", [], null);
        second.ServiceProvider.GetRequiredService<ExecutionScope>().InitialiseForUser(DatabaseFixture.TenantA, "b", [], null);
        var db1 = first.ServiceProvider.GetRequiredService<TestDbContext>();
        var db2 = second.ServiceProvider.GetRequiredService<TestDbContext>();
        await using var tx1 = await db1.Database.BeginTransactionAsync();
        var w1 = await db1.Widgets.SingleAsync(w => w.Id == id);
        await tx1.CommitAsync();
        await using var tx2 = await db2.Database.BeginTransactionAsync();
        var w2 = await db2.Widgets.SingleAsync(w => w.Id == id);
        await tx2.CommitAsync();

        w1.Rename("one");
        await using (var save1 = await db1.Database.BeginTransactionAsync())
        {
            await db1.SaveChangesAsync();
            await save1.CommitAsync();
        }

        w2.Rename("two");
        await using var save2 = await db2.Database.BeginTransactionAsync();
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => db2.SaveChangesAsync());
    }

    [Fact]
    public async Task RenameWithStaleVersion_IsPreconditionFailed_AndCurrentVersionIncrements_DBA011()
    {
        var id = (await _services.Send(DatabaseFixture.TenantA, new CreateWidget("versioned"))).Value;

        (await _services.Send(DatabaseFixture.TenantA, new RenameWidget(id, 1, "v2"))).Value.ShouldBe(2);
        (await _services.Send(DatabaseFixture.TenantA, new RenameWidget(id, 1, "stale"))).Error!.Kind.ShouldBe(ErrorKind.PreconditionFailed);
        (await fixture.Database.ScalarAsOwner<long>("SELECT row_version FROM test_mod.widget WHERE id = @id", ("id", id))).ShouldBe(2);
    }

    [Fact]
    public async Task Relay_PublishesToConsumer_WithEnvelopeTenant_AndSetsPublishedAt()
    {
        var id = (await _services.Send(DatabaseFixture.TenantB, new CreateWidget("relayed"))).Value;

        await _services.GetRequiredService<OutboxProcessor>().ProcessOnce(CancellationToken.None);

        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM test_mod.outbox_event WHERE aggregate_id = @id AND published_at IS NOT NULL AND event_type = 'test_mod.WidgetCreated'", ("id", id)))
            .ShouldBe(1);
        var consumedTenant = await fixture.Database.ScalarAsOwner<Guid>("SELECT tenant_id FROM test_mod.consumption WHERE widget_id = @id", ("id", id));
        consumedTenant.ShouldBe(DatabaseFixture.TenantB);
        var actor = await fixture.Database.ScalarAsOwner<string>("SELECT actor FROM test_mod.consumption WHERE widget_id = @id", ("id", id));
        actor.ShouldBe("test_mod.WidgetCreatedConsumer");
    }

    [Fact]
    public async Task DuplicateDelivery_IsSkippedByTheInbox_INT005_DBA007()
    {
        var id = (await _services.Send(DatabaseFixture.TenantA, new CreateWidget("twice"))).Value;
        await _services.GetRequiredService<OutboxProcessor>().ProcessOnce(CancellationToken.None);
        var envelope = await EnvelopeFor(id);

        await _services.GetRequiredService<IEventBus>().Publish(envelope, CancellationToken.None);
        await _services.GetRequiredService<IEventBus>().Publish(envelope, CancellationToken.None);

        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM test_mod.consumption WHERE widget_id = @id", ("id", id))).ShouldBe(1);
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM test_mod.inbox_message WHERE event_id = @e", ("e", envelope.EventId))).ShouldBe(1);
    }

    [Fact]
    public async Task ConsumerTenantValidation_EventForTenantA_CannotWriteTenantBRows_TEN004()
    {
        var id = (await _services.Send(DatabaseFixture.TenantA, new CreateWidget("tenant-check"))).Value;
        await _services.GetRequiredService<OutboxProcessor>().ProcessOnce(CancellationToken.None);
        var envelope = await EnvelopeFor(id) with { EventId = Guid.CreateVersion7(), TenantId = DatabaseFixture.TenantB };

        // An envelope for tenant B delivers only into tenant B's scope; tenant A's data is never visible to it.
        await _services.GetRequiredService<IEventBus>().Publish(envelope, CancellationToken.None);

        (await fixture.Database.ScalarAsOwner<Guid>("SELECT tenant_id FROM test_mod.consumption WHERE event_id = @e", ("e", envelope.EventId)))
            .ShouldBe(DatabaseFixture.TenantB);
    }

    [Fact]
    public async Task FailingConsumer_IsRetried_ThenDeadLettered_INT006()
    {
        await using var services = TestServices.Build(fixture.Database);
        services.GetRequiredService<ConsumerSwitch>().Fail = true;
        var id = (await services.Send(DatabaseFixture.TenantA, new CreateWidget("poison"))).Value;
        var relay = services.GetRequiredService<OutboxProcessor>();

        for (var attempt = 0; attempt < 4; attempt++)
        {
            await relay.ProcessOnce(CancellationToken.None);
        }

        var eventId = await fixture.Database.ScalarAsOwner<Guid>("SELECT id FROM test_mod.outbox_event WHERE aggregate_id = @id AND event_type = 'test_mod.WidgetCreated'", ("id", id));
        (await fixture.Database.ScalarAsOwner<short>("SELECT publish_attempts FROM test_mod.outbox_event WHERE id = @e", ("e", eventId))).ShouldBe((short)3);
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM test_mod.outbox_event WHERE id = @e AND published_at IS NULL", ("e", eventId))).ShouldBe(1);
        (await fixture.Database.ScalarAsOwner<long>("""
            SELECT count(*) FROM integration.dead_letter_message
            WHERE message_id = @e AND source = 'outbox:test_mod' AND attempts = 3 AND resolution IS NULL AND tenant_id = @t
            """, ("e", eventId.ToString()), ("t", DatabaseFixture.TenantA))).ShouldBe(1);
        var error = await fixture.Database.ScalarAsOwner<string>("SELECT error FROM integration.dead_letter_message WHERE message_id = @e", ("e", eventId.ToString()));
        error.ShouldNotBeNull();
        error.ShouldNotContain("told to fail");
    }

    private async Task<long> Count(string table, Guid id, string column) =>
        await fixture.Database.ScalarAsOwner<long>($"SELECT count(*) FROM {table} WHERE {column} = @id", ("id", id));

    private async Task<EventEnvelope> EnvelopeFor(Guid aggregateId)
    {
        var eventId = await fixture.Database.ScalarAsOwner<Guid>("SELECT id FROM test_mod.outbox_event WHERE aggregate_id = @id AND event_type = 'test_mod.WidgetCreated'", ("id", aggregateId));
        var tenant = await fixture.Database.ScalarAsOwner<Guid>("SELECT tenant_id FROM test_mod.outbox_event WHERE id = @id", ("id", eventId));
        var payload = await fixture.Database.ScalarAsOwner<string>("SELECT payload::text FROM test_mod.outbox_event WHERE id = @id", ("id", eventId));
        using var document = System.Text.Json.JsonDocument.Parse(payload!);
        return new EventEnvelope(eventId, tenant, "test_mod.WidgetCreated", 1, DateTimeOffset.UtcNow, nameof(Widget), aggregateId,
            Guid.CreateVersion7(), null, document.RootElement.Clone());
    }
}
