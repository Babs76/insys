/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Application
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
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;

namespace Insys.BuildingBlocks.Application.Messaging;

/// <summary>Delivers an event to its subscribers. In-process for now; a broker adapter can replace it (MESSAGE_BROKER).</summary>
public interface IEventBus
{
    /// <summary>Delivers the event to every consumer and returns each consumer's outcome (ADR-022).</summary>
    Task<IReadOnlyList<ConsumerDelivery>> Deliver(EventEnvelope envelope, CancellationToken cancellationToken);

    /// <summary>Delivers the event to one consumer only: a retry or release from the delivery ledger (ADR-022).</summary>
    Task<ConsumerDelivery> DeliverTo(EventEnvelope envelope, string consumer, CancellationToken cancellationToken);
}

public static class EventBusExtensions
{
    /// <summary>Delivers the event and throws when any consumer failed. A parked or waiting consumer is not a failure.</summary>
    public static async Task Publish(this IEventBus bus, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var failures = (await bus.Deliver(envelope, cancellationToken)).Where(d => d.Status == DeliveryStatus.Failed).ToList();
        if (failures.Count > 0)
        {
            throw new AggregateException($"{failures.Count} consumer(s) failed for event {envelope.EventId}.", failures.Select(f => f.Error!));
        }
    }
}

/// <summary>
/// A policy in a consuming module. Runs inside the consumer's transaction after the inbox has recorded the event,
/// so a duplicate delivery is skipped (INT-005, DBA-007). Throw to have the event retried; throw
/// <see cref="PrerequisiteMissingException"/> when the tenant's configuration is missing, to have it parked (ADR-022).
/// </summary>
public interface IIntegrationEventHandler<in TEvent>
    where TEvent : IIntegrationEvent
{
    Task Handle(TEvent integrationEvent, EventEnvelope envelope, CancellationToken cancellationToken);
}

/// <summary>Maps a module's domain events to the integration events it publishes in its Contracts project.</summary>
public interface IDomainEventMapper
{
    /// <summary>Returns the integration event, or null when the mapper does not handle this domain event.</summary>
    IIntegrationEvent? Map(IDomainEvent domainEvent);
}

/// <summary>
/// The only texts a dead letter's error is made from (BR-017, D-28): an exception's type names, an HTTP status, or a fixed
/// sentence. Never an exception's message, which can quote the payload and with it personal data (POPIA).
/// </summary>
public static class DeadLetterErrors
{
    /// <summary>The parking ledger's error, when a consumer failed a delivery more times than its limit.</summary>
    public const string AttemptLimitReached = "Delivery failed after the attempt limit; see the consumer's logs.";

    /// <summary>The exception's full type name, or every inner exception's for an aggregate; never a message.</summary>
    public static string Of(Exception exception) =>
        exception is AggregateException aggregate
            ? string.Join("; ", aggregate.Flatten().InnerExceptions.Select(e => e.GetType().FullName))
            : exception.GetType().FullName ?? exception.GetType().Name;

    /// <summary>A webhook attempt with no response: the exception's short type name (a timeout or a connection failure).</summary>
    public static string NoResponse(Exception exception) => exception.GetType().Name;

    /// <summary>A webhook attempt answered with a status other than 2xx.</summary>
    public static string Status(int status) => $"HTTP {status}";
}

/// <summary>Records messages that failed delivery after all retries (INT-006). Implemented by the Integration module.</summary>
public interface IDeadLetterSink
{
    Task DeadLetter(EventEnvelope envelope, string source, string error, short attempts, DateTimeOffset firstFailedAt, CancellationToken cancellationToken);
}
