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
using System.Text.Json;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Insys.BuildingBlocks.Infrastructure.Messaging;

/// <summary>One consumer of one event type, registered by the consuming module.</summary>
public sealed class EventSubscription(string eventType, string consumer, Type eventClrType, Func<IServiceProvider, object, EventEnvelope, CancellationToken, Task> invoke)
{
    public string EventType { get; } = eventType;

    public string Consumer { get; } = consumer;

    public Type EventClrType { get; } = eventClrType;

    public Task Invoke(IServiceProvider services, object integrationEvent, EventEnvelope envelope, CancellationToken cancellationToken) =>
        invoke(services, integrationEvent, envelope, cancellationToken);
}

/// <summary>
/// Delivers events to subscribers in this process. Each consumer runs in its own DI scope whose tenant is the
/// envelope's tenant, as a service identity, so a consumer can only touch that tenant's data (TEN-004), and under
/// Outbox:AttemptTimeout. Each consumer's outcome is separate (ADR-022): a missing prerequisite parks the delivery in
/// the ledger, a later event for a record with a parked or failing delivery to that consumer waits behind it, and a
/// failure is retried by the outbox. Consumers that already succeeded skip a repeat through the inbox.
/// </summary>
public sealed class InProcessEventBus(
    IEnumerable<EventSubscription> subscriptions,
    IServiceScopeFactory scopeFactory,
    IDeliveryLedger ledger,
    IOptions<OutboxOptions> options,
    ILogger<InProcessEventBus> logger) : IEventBus
{
    private readonly ILookup<string, EventSubscription> _subscriptions = subscriptions.ToLookup(s => s.EventType, StringComparer.Ordinal);

    public async Task<IReadOnlyList<ConsumerDelivery>> Deliver(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var entries = ledger.Enabled ? await ledger.ForRecord(envelope, cancellationToken) : [];
        var outcomes = new List<ConsumerDelivery>();
        foreach (var subscription in SubscriptionsFor(envelope.EventType))
        {
            var consumer = subscription.Consumer;
            var own = entries.FirstOrDefault(e => e.Consumer == consumer && e.EventId == envelope.EventId);
            switch (own?.Status)
            {
                case LedgerStatus.Parked:
                    outcomes.Add(new ConsumerDelivery(consumer, DeliveryStatus.Parked));
                    continue;
                case LedgerStatus.Waiting:
                    outcomes.Add(new ConsumerDelivery(consumer, DeliveryStatus.Waiting));
                    continue;
                case LedgerStatus.Retrying or LedgerStatus.Resolved or LedgerStatus.Discarded:
                    outcomes.Add(new ConsumerDelivery(consumer, DeliveryStatus.Skipped));
                    continue;
                case null when entries.Any(e => e.Consumer == consumer && e.EventId != envelope.EventId && e.Blocks):
                    // Order per record (ADR-022 decision 3): an earlier delivery for this record is parked or failing.
                    await ledger.Wait(envelope, consumer, cancellationToken);
                    outcomes.Add(new ConsumerDelivery(consumer, DeliveryStatus.Waiting));
                    continue;
            }

            var outcome = await Attempt(subscription, envelope, cancellationToken);
            await Settle(envelope, outcome, held: own is not null, ledgerOwned: false, cancellationToken);
            outcomes.Add(outcome);
        }

        return outcomes;
    }

    public async Task<ConsumerDelivery> DeliverTo(EventEnvelope envelope, string consumer, CancellationToken cancellationToken)
    {
        var subscription = SubscriptionsFor(envelope.EventType).FirstOrDefault(s => s.Consumer == consumer)
            ?? throw new InvalidOperationException($"Consumer {consumer} does not subscribe to {envelope.EventType}.");
        var outcome = await Attempt(subscription, envelope, cancellationToken);
        await Settle(envelope, outcome, held: true, ledgerOwned: true, cancellationToken);
        return outcome;
    }

    private IEnumerable<EventSubscription> SubscriptionsFor(string eventType) =>
        _subscriptions[eventType].Concat(_subscriptions[AnyIntegrationEvent.Wildcard]);

    /// <summary>Records the outcome in the ledger; a resolved delivery releases the record's waiting deliveries, in order.</summary>
    private async Task Settle(EventEnvelope envelope, ConsumerDelivery outcome, bool held, bool ledgerOwned, CancellationToken cancellationToken)
    {
        if (!ledger.Enabled)
        {
            return;
        }

        switch (outcome.Status)
        {
            case DeliveryStatus.Parked:
                await ledger.Park(envelope, outcome.Consumer, outcome.Code!, outcome.Item, cancellationToken);
                return;
            case DeliveryStatus.Failed:
                await ledger.Failing(envelope, outcome.Consumer, ledgerOwned, cancellationToken);
                return;
            case DeliveryStatus.Processed when held:
                await ledger.Resolve(envelope, outcome.Consumer, cancellationToken);
                await Release(envelope.TenantId, outcome.Consumer, envelope.AggregateType, envelope.AggregateId, cancellationToken);
                return;
        }
    }

    /// <summary>Delivers the record's waiting deliveries to the consumer, oldest first, until one parks or fails.</summary>
    public async Task Release(Guid tenantId, string consumer, string aggregateType, Guid aggregateId, CancellationToken cancellationToken)
    {
        while (await ledger.ClaimNextWaiting(tenantId, consumer, aggregateType, aggregateId, cancellationToken) is { } next)
        {
            var subscription = SubscriptionsFor(next.EventType).FirstOrDefault(s => s.Consumer == consumer);
            if (subscription is null)
            {
                // The consumer no longer subscribes to this event type: nothing to deliver.
                await ledger.Resolve(next, consumer, cancellationToken);
                continue;
            }

            var outcome = await Attempt(subscription, next, cancellationToken);
            switch (outcome.Status)
            {
                case DeliveryStatus.Processed:
                    await ledger.Resolve(next, consumer, cancellationToken);
                    continue;
                case DeliveryStatus.Parked:
                    await ledger.Park(next, consumer, outcome.Code!, outcome.Item, cancellationToken);
                    return;
                default:
                    await ledger.Failing(next, consumer, ledgerOwned: true, cancellationToken);
                    return;
            }
        }
    }

    private async Task<ConsumerDelivery> Attempt(EventSubscription subscription, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        // ADR-022 decision 8: a slow failure costs every tenant's relay pass at most the attempt timeout.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Value.AttemptTimeout);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ExecutionScope>()
                .InitialiseForService(envelope.TenantId, subscription.Consumer, envelope.CorrelationId, envelope.EventId);
            var integrationEvent = subscription.EventClrType == typeof(AnyIntegrationEvent)
                ? new AnyIntegrationEvent(envelope.Payload)
                : envelope.Payload.Deserialize(subscription.EventClrType, ModuleDbContext.EventJson)
                  ?? throw new InvalidOperationException($"Event {envelope.EventId} has an empty payload.");
            await subscription.Invoke(scope.ServiceProvider, integrationEvent, envelope, timeout.Token);
            return new ConsumerDelivery(subscription.Consumer, DeliveryStatus.Processed);
        }
        catch (Exception ex) when (Unwrap(ex) is PrerequisiteMissingException missing && ledger.Enabled)
        {
            logger.LogInformation("Consumer {Consumer} parked event {EventId} of type {EventType}: {Code} {Item}",
                subscription.Consumer, envelope.EventId, envelope.EventType, missing.Code, missing.Item);
            return new ConsumerDelivery(subscription.Consumer, DeliveryStatus.Parked, missing, missing.Code, missing.Item);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Consumer {Consumer} timed out after {Timeout} for event {EventId} of type {EventType}",
                subscription.Consumer, options.Value.AttemptTimeout, envelope.EventId, envelope.EventType);
            return new ConsumerDelivery(subscription.Consumer, DeliveryStatus.Failed, new TimeoutException("The delivery attempt timed out.", ex));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Log the type and IDs only: payloads and exception messages may carry sensitive values.
            logger.LogWarning("Consumer {Consumer} failed for event {EventId} of type {EventType}: {ExceptionType}",
                subscription.Consumer, envelope.EventId, envelope.EventType, ex.GetType().Name);
            return new ConsumerDelivery(subscription.Consumer, DeliveryStatus.Failed, ex);
        }
    }

    private static Exception Unwrap(Exception ex)
    {
        while (ex is AggregateException { InnerExceptions.Count: 1 } or System.Reflection.TargetInvocationException && ex.InnerException is not null)
        {
            ex = ex.InnerException!;
        }

        return ex;
    }
}
