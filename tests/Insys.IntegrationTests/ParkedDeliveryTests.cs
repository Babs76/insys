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
using System.Text.Json;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Contracts.StaticData;
using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Infrastructure.Messaging;
using Insys.Integration.Application.Parking;
using Insys.Integration.Infrastructure.Parking;
using Insys.IntegrationTests.TestModule;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Insys.IntegrationTests;

/// <summary>
/// ADR-022 against the real schema, with the test module's ledger consumer: a delivery whose tenant configuration is
/// missing is parked, later events for the same record wait behind it in order, configuration being published retries
/// it, and nothing is ever applied twice (INT-005, INT-006).
/// </summary>
public sealed class ParkedDeliveryTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private const string Consumer = "test_mod.WidgetLedgerConsumer";
    private ServiceProvider _services = null!;
    private ConsumerSwitch _switch = null!;

    public ValueTask InitializeAsync()
    {
        _services = TestServices.Build(fixture.Database, s => s["Outbox:AttemptTimeout"] = "00:00:01");
        _switch = _services.GetRequiredService<ConsumerSwitch>();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _services.DisposeAsync();

    [Fact]
    public async Task ParkedEvent_HoldsLaterEventsForItsRecordOnly_AndIsRetriedInOrderOnceConfigurationIsPublished_ADR022()
    {
        var widget = await CreateWidget("parked", missing: true);
        var other = await CreateWidget("unaffected", missing: false);
        await Relay();

        // The parked consumer holds nothing else: the other consumer and the other record are delivered, and the event counts as published.
        (await Status(widget, "test_mod.WidgetCreated")).ShouldBe("PARKED");
        (await Applied(other)).ShouldBe("created");
        (await Applied(widget)).ShouldBeNull();
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM test_mod.consumption WHERE widget_id = @w", ("w", widget))).ShouldBe(1);
        (await fixture.Database.ScalarAsOwner<long>(
            "SELECT count(*) FROM test_mod.outbox_event WHERE aggregate_id = @w AND published_at IS NULL", ("w", widget))).ShouldBe(0);

        // Two renames of the parked record wait behind it, in order; a rename of the other record goes ahead.
        await Rename(widget, "second");
        await Rename(widget, "third");
        await Rename(other, "renamed");
        await Relay();
        (await Statuses(widget)).ShouldBe("PARKED,WAITING,WAITING");
        (await Applied(other)).ShouldBe("created,renamed");

        // The configuration arrives but nothing has said so yet: the periodic check is not due, so the delivery stays parked.
        _switch.Missing.TryRemove(widget, out _);
        var job = _services.GetRequiredService<ParkingJob>();
        (await job.RunForTenant(DatabaseFixture.TenantA, CancellationToken.None)).Retried.ShouldBe(0);
        (await Applied(widget)).ShouldBeNull();

        // Publishing configuration in the tenant retries its parked deliveries; the waiting ones are released in order.
        await PublishConfiguration(DatabaseFixture.TenantA);
        var pass = await job.RunForTenant(DatabaseFixture.TenantA, CancellationToken.None);
        pass.Retried.ShouldBe(1);
        pass.Resolved.ShouldBe(1);
        (await Applied(widget)).ShouldBe("created,second,third");
        (await Statuses(widget)).ShouldBe("RESOLVED,RESOLVED,RESOLVED");
    }

    [Fact]
    public async Task ParkedEvent_IsNeverAppliedTwice_OnReplay_OnARepeatedRetry_OrAfterAnInstanceStopsMidRetry_INT005()
    {
        var widget = await CreateWidget("replayed", missing: true);
        await Relay();
        var envelope = await Envelope(widget, "test_mod.WidgetCreated");
        _switch.Missing.TryRemove(widget, out _);
        await PublishConfiguration(DatabaseFixture.TenantA);
        var job = _services.GetRequiredService<ParkingJob>();
        await job.RunForTenant(DatabaseFixture.TenantA, CancellationToken.None);
        (await Applied(widget)).ShouldBe("created");

        // The outbox replays the event (a dead letter replayed, say): the resolved delivery is skipped.
        var bus = _services.GetRequiredService<IEventBus>();
        (await bus.Deliver(envelope, CancellationToken.None)).Single(d => d.Consumer == Consumer).Status.ShouldBe(DeliveryStatus.Skipped);

        // A manual retry delivers through the consumer's inbox, which skips an event it already applied.
        (await bus.DeliverTo(envelope, Consumer, CancellationToken.None)).Status.ShouldBe(DeliveryStatus.Processed);

        // An instance stopped after the consumer committed but before the outcome was written: the row is left retrying.
        await fixture.Database.ExecuteAsOwner("""
            UPDATE integration.parked_delivery SET status = 'RETRYING', resolved_at = NULL, last_attempt_at = now() - interval '1 hour'
            WHERE event_id = @e AND consumer = @c
            """, ("e", envelope.EventId), ("c", Consumer));
        var recovered = await job.RunForTenant(DatabaseFixture.TenantA, CancellationToken.None);
        recovered.Retried.ShouldBe(1);
        (await Status(widget, "test_mod.WidgetCreated")).ShouldBe("RESOLVED");

        (await Applied(widget)).ShouldBe("created");
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM test_mod.inbox_message WHERE event_id = @e AND consumer = @c",
            ("e", envelope.EventId), ("c", Consumer))).ShouldBe(1);
    }

    [Fact]
    public async Task FailingDelivery_AlsoHoldsLaterEventsForItsRecord_SoTheyCannotOvertakeIt_ADR022()
    {
        var widget = await CreateWidget("failing", missing: false, failing: true);
        await Rename(widget, "after");
        await Relay();

        // The failure stays the outbox's to retry; the rename waits behind it instead of overtaking it.
        (await Statuses(widget)).ShouldBe("FAILING,WAITING");
        (await Applied(widget)).ShouldBeNull();

        _switch.Failing.TryRemove(widget, out _);
        await Relay();
        (await Applied(widget)).ShouldBe("created,after");
        (await Statuses(widget)).ShouldBe("RESOLVED,RESOLVED");
    }

    [Fact]
    public async Task SlowConsumer_IsCutOffAtTheAttemptTimeout_AndRetriedAsAFailure_ADR022()
    {
        var widget = await CreateWidget("slow", missing: false);
        _switch.Slow[widget] = true;
        var envelope = await Envelope(widget, "test_mod.WidgetCreated");

        var started = DateTimeOffset.UtcNow;
        var outcome = (await _services.GetRequiredService<IEventBus>().Deliver(envelope, CancellationToken.None)).Single(d => d.Consumer == Consumer);

        (DateTimeOffset.UtcNow - started).ShouldBeLessThan(TimeSpan.FromSeconds(10));
        outcome.Status.ShouldBe(DeliveryStatus.Failed);
        outcome.Error.ShouldBeOfType<TimeoutException>();
        (await Status(widget, "test_mod.WidgetCreated")).ShouldBe("FAILING");
    }

    [Fact]
    public async Task ParkedDelivery_IsEscalatedOnce_ThenToTheTenantAdministrator_AndShowsOnHealth_OQ072()
    {
        var widget = await CreateWidget("escalated", missing: true);
        await Relay();
        var job = _services.GetRequiredService<ParkingJob>();
        await job.RunForTenant(DatabaseFixture.TenantA, CancellationToken.None);

        // Not a money consumer: the first escalation is a business day after parking, never sooner than the clock allows.
        var next = await At(
            "SELECT next_escalation_at FROM integration.parked_delivery WHERE aggregate_id = @w AND consumer = @c", ("w", widget), ("c", Consumer));
        var parkedAt = await At(
            "SELECT parked_at FROM integration.parked_delivery WHERE aggregate_id = @w AND consumer = @c", ("w", widget), ("c", Consumer));
        (next - parkedAt).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromHours(9));
        (await Health()).Status.ShouldBe(HealthStatus.Degraded);

        foreach (var (level, role) in new[] { (1, "TENANT_ADMIN"), (2, "TENANT_ADMIN") })
        {
            await fixture.Database.ExecuteAsOwner(
                "UPDATE integration.parked_delivery SET next_escalation_at = now() - interval '1 minute' WHERE aggregate_id = @w AND consumer = @c", ("w", widget), ("c", Consumer));
            (await job.RunForTenant(DatabaseFixture.TenantA, CancellationToken.None)).Escalated.ShouldBe(1);
            (await fixture.Database.ScalarAsOwner<short>(
                "SELECT escalation_level FROM integration.parked_delivery WHERE aggregate_id = @w AND consumer = @c", ("w", widget), ("c", Consumer))).ShouldBe((short)level);
            var payload = await fixture.Database.ScalarAsOwner<string>("""
                SELECT payload::text FROM integration.outbox_event
                WHERE event_type = 'integration.ParkedDeliveryEscalated' AND (payload ->> 'level')::int = @l AND payload ->> 'consumer' = @c
                ORDER BY occurred_at DESC LIMIT 1
                """, ("l", level), ("c", Consumer));
            using var document = JsonDocument.Parse(payload!);
            document.RootElement.GetProperty("targetRole").GetString().ShouldBe(role);
            document.RootElement.GetProperty("code").GetString().ShouldBe(WidgetLedgerConsumer.MissingCode);
            document.RootElement.GetProperty("item").GetString().ShouldBe("BLUE");
        }

        // Escalated as far as it goes: never dropped or dead-lettered automatically, and /health is Unhealthy for platform operations.
        (await job.RunForTenant(DatabaseFixture.TenantA, CancellationToken.None)).Escalated.ShouldBe(0);
        (await Status(widget, "test_mod.WidgetCreated")).ShouldBe("PARKED");
        var health = await Health();
        health.Status.ShouldBe(HealthStatus.Unhealthy);
        // The alert names the tenant by its code and the missing configuration by code and item, with numbers for the Cloud Eye probe.
        var tenants = (SortedDictionary<string, List<string>>)health.Data["tenants"];
        tenants["TENA"].ShouldContain(v => v.StartsWith($"{WidgetLedgerConsumer.MissingCode} BLUE x1 ({Consumer}), escalated level 2", StringComparison.Ordinal));
        health.Description.ShouldNotBeNull().ShouldContain($"TENA {WidgetLedgerConsumer.MissingCode} BLUE");
        ((int)health.Data["parked"]).ShouldBeGreaterThanOrEqualTo(1);
        ((int)health.Data["escalated"]).ShouldBeGreaterThanOrEqualTo(1);

        // Clean up: discard it, so the other tests' health checks start from nothing.
        await Discard(widget);
    }

    [Fact]
    public async Task Discard_NeedsAReason_IsAudited_AndReleasesTheDeliveriesWaitingBehindIt_ADR022()
    {
        var widget = await CreateWidget("discarded", missing: true);
        await Relay();
        _switch.Missing.TryRemove(widget, out _);
        await Rename(widget, "kept");
        await Relay();

        var id = await fixture.Database.ScalarAsOwner<Guid>(
            "SELECT id FROM integration.parked_delivery WHERE aggregate_id = @w AND consumer = @c AND status = 'PARKED'", ("w", widget), ("c", Consumer));
        (await Send(new DiscardParkedDelivery(id, 1, ""))).IsFailure.ShouldBeTrue();

        (await Discard(widget)).IsSuccess.ShouldBeTrue();
        await Relay();
        (await fixture.Database.ScalarAsOwner<long>("""
            SELECT count(*) FROM audit.audit_event
            WHERE target_type = 'ParkedDelivery' AND target_id = @id AND operation = 'DiscardParkedDelivery' AND outcome = 'SUCCESS'
            """, ("id", id.ToString()))).ShouldBe(1);

        await _services.GetRequiredService<ParkingJob>().RunForTenant(DatabaseFixture.TenantA, CancellationToken.None);
        (await Statuses(widget)).ShouldBe("DISCARDED,RESOLVED");
        (await Applied(widget)).ShouldBe("kept");
    }

    private async Task<Result<long>> Discard(Guid widget)
    {
        var (id, version) = (await fixture.Database.ScalarAsOwner<Guid>(
                "SELECT id FROM integration.parked_delivery WHERE aggregate_id = @w AND consumer = @c AND status = 'PARKED'", ("w", widget), ("c", Consumer)),
            await fixture.Database.ScalarAsOwner<long>(
                "SELECT row_version FROM integration.parked_delivery WHERE aggregate_id = @w AND consumer = @c AND status = 'PARKED'", ("w", widget), ("c", Consumer)));
        return await Send(new DiscardParkedDelivery(id, version, "Colour withdrawn from the range; the event no longer applies."));
    }

    private async Task<Result<long>> Send(DiscardParkedDelivery command)
    {
        await using var scope = _services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ExecutionScope>().InitialiseForUser(DatabaseFixture.TenantA, Guid.CreateVersion7().ToString(), [], null);
        return await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(command);
    }

    private async Task<Guid> CreateWidget(string name, bool missing, bool failing = false)
    {
        // The widget's ID is known only once it exists, so the switch names it before the relay runs.
        var id = (await _services.Send(DatabaseFixture.TenantA, new CreateWidget(name))).Value;
        if (missing)
        {
            _switch.Missing[id] = true;
        }

        if (failing)
        {
            _switch.Failing[id] = true;
        }

        return id;
    }

    private async Task Rename(Guid widget, string name)
    {
        var version = await fixture.Database.ScalarAsOwner<long>("SELECT row_version FROM test_mod.widget WHERE id = @w", ("w", widget));
        (await _services.Send(DatabaseFixture.TenantA, new RenameWidget(widget, version, name))).IsSuccess.ShouldBeTrue();
    }

    private Task Relay() => _services.GetRequiredService<OutboxProcessor>().ProcessOnce(CancellationToken.None);

    private async Task PublishConfiguration(Guid tenantId)
    {
        var activated = new StaticDataVersionActivated(null, new StaticRecordRef("tenant", "cfg_locale_defaults", "LOCALE_DEFAULTS", "tenant", "locale-defaults", Guid.CreateVersion7(), "DEFAULT", 2, false), DateOnly.FromDateTime(DateTime.UtcNow), null);
        var envelope = new EventEnvelope(Guid.CreateVersion7(), tenantId, "sdm.StaticDataVersionActivated", 1, DateTimeOffset.UtcNow, "StaticRecord", Guid.CreateVersion7(),
            Guid.CreateVersion7(), null, JsonSerializer.SerializeToElement(activated, JsonSerializerOptions.Web));
        await _services.GetRequiredService<IEventBus>().Publish(envelope, CancellationToken.None);
    }

    private Task<string?> Applied(Guid widget) =>
        fixture.Database.ScalarAsOwner<string>("SELECT string_agg(what, ',' ORDER BY seq) FROM test_mod.ledger_consumption WHERE widget_id = @w", ("w", widget));

    private Task<string?> Statuses(Guid widget) =>
        fixture.Database.ScalarAsOwner<string>(
            "SELECT string_agg(status, ',' ORDER BY occurred_at, event_id) FROM integration.parked_delivery WHERE aggregate_id = @w AND consumer = @c",
            ("w", widget), ("c", Consumer));

    private Task<string?> Status(Guid widget, string eventType) =>
        fixture.Database.ScalarAsOwner<string>(
            "SELECT status FROM integration.parked_delivery WHERE aggregate_id = @w AND consumer = @c AND event_type = @t", ("w", widget), ("c", Consumer), ("t", eventType));

    private async Task<EventEnvelope> Envelope(Guid widget, string eventType)
    {
        var id = await fixture.Database.ScalarAsOwner<Guid>("SELECT id FROM test_mod.outbox_event WHERE aggregate_id = @w AND event_type = @t", ("w", widget), ("t", eventType));
        var payload = await fixture.Database.ScalarAsOwner<string>("SELECT payload::text FROM test_mod.outbox_event WHERE id = @id", ("id", id));
        var occurred = await At("SELECT occurred_at FROM test_mod.outbox_event WHERE id = @id", ("id", id));
        using var document = JsonDocument.Parse(payload!);
        return new EventEnvelope(id, DatabaseFixture.TenantA, eventType, 1, occurred, nameof(Widget), widget, Guid.CreateVersion7(), null, document.RootElement.Clone());
    }

    private async Task<DateTimeOffset> At(string sql, params (string Name, object Value)[] parameters) =>
        new(DateTime.SpecifyKind(await fixture.Database.ScalarAsOwner<DateTime>(sql, parameters), DateTimeKind.Utc));

    private Task<HealthCheckResult> Health() =>
        ActivatorUtilities.CreateInstance<ParkedDeliveriesHealthCheck>(_services).CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);
}
