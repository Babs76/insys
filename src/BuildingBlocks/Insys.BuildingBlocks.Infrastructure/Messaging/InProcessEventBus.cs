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
/// envelope's tenant, as a service identity, so a consumer can only touch that tenant's data (TEN-004).
/// If any consumer fails the publish fails and the outbox relay retries; consumers that already succeeded skip the
/// duplicate through the inbox.
/// </summary>
public sealed class InProcessEventBus(IEnumerable<EventSubscription> subscriptions, IServiceScopeFactory scopeFactory, ILogger<InProcessEventBus> logger) : IEventBus
{
    private readonly ILookup<string, EventSubscription> _subscriptions = subscriptions.ToLookup(s => s.EventType, StringComparer.Ordinal);

    public async Task Publish(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        List<Exception>? failures = null;
        foreach (var subscription in _subscriptions[envelope.EventType].Concat(_subscriptions[AnyIntegrationEvent.Wildcard]))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<ExecutionScope>()
                    .InitialiseForService(envelope.TenantId, subscription.Consumer, envelope.CorrelationId, envelope.EventId);
                var integrationEvent = subscription.EventClrType == typeof(AnyIntegrationEvent)
                    ? new AnyIntegrationEvent(envelope.Payload)
                    : envelope.Payload.Deserialize(subscription.EventClrType, ModuleDbContext.EventJson)
                      ?? throw new InvalidOperationException($"Event {envelope.EventId} has an empty payload.");
                await subscription.Invoke(scope.ServiceProvider, integrationEvent, envelope, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Log the type and IDs only: payloads and exception messages may carry sensitive values.
                logger.LogWarning("Consumer {Consumer} failed for event {EventId} of type {EventType}: {ExceptionType}",
                    subscription.Consumer, envelope.EventId, envelope.EventType, ex.GetType().Name);
                (failures ??= []).Add(ex);
            }
        }

        if (failures is not null)
        {
            throw new AggregateException($"{failures.Count} consumer(s) failed for event {envelope.EventId}.", failures);
        }
    }
}
