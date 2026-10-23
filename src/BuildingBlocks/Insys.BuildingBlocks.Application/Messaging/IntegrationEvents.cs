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
    Task Publish(EventEnvelope envelope, CancellationToken cancellationToken);
}

/// <summary>
/// A policy in a consuming module. Runs inside the consumer's transaction after the inbox has recorded the event,
/// so a duplicate delivery is skipped (INT-005, DBA-007). Throw to have the event retried.
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

/// <summary>Records messages that failed delivery after all retries (INT-006). Implemented by the Integration module.</summary>
public interface IDeadLetterSink
{
    Task DeadLetter(EventEnvelope envelope, string source, string error, short attempts, DateTimeOffset firstFailedAt, CancellationToken cancellationToken);
}
