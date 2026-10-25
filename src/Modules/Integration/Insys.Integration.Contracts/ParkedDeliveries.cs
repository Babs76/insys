/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Integration
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

namespace Insys.Integration.Contracts;

/// <summary>
/// Parked deliveries of one kind (consumer, missing configuration code and item) in a tenant reached an escalation
/// level (ADR-022 decision 6). Workflow opens a task for the target role. Names configuration only, never a party.
/// </summary>
/// <param name="ParkedDeliveryId">The oldest parked delivery of the kind; the task's business reference.</param>
/// <param name="Level">1 or 2.</param>
/// <param name="DueAt">When the next escalation falls due, or the task's due time at the last level.</param>
[IntegrationEvent("integration.ParkedDeliveryEscalated")]
public sealed record ParkedDeliveryEscalated(Guid ParkedDeliveryId, int Level, string TargetRole, string Consumer, string Code, string? Item, int Count,
    DateTimeOffset DueAt) : IIntegrationEvent
{
    /// <summary>The Workflow task's business reference type.</summary>
    public const string BusinessRefType = "PARKED_DELIVERY";
}

/// <summary>
/// A delivery was parked for the first time (ADR-022): the consumer could not apply the event for the reason the code and
/// item name. The module that owns the event acts on it where it needs to, for example Workflow marking a granted
/// approval not applied (APPROVAL_NOT_APPLIED, D-5). Names configuration and IDs only, never a party or payload.
/// </summary>
/// <param name="EventType">The parked event's type, for example workflow.ApprovalGranted.</param>
[IntegrationEvent("integration.DeliveryParked")]
public sealed record DeliveryParked(Guid ParkedDeliveryId, string Consumer, string Code, string? Item, string EventType, Guid EventId) : IIntegrationEvent;

/// <summary>A parked delivery was retried and the consumer applied it after all (ADR-022); the code and item are why it had parked.</summary>
[IntegrationEvent("integration.ParkedDeliveryResolved")]
public sealed record ParkedDeliveryResolved(Guid ParkedDeliveryId, string Consumer, string Code, string? Item, string EventType, Guid EventId) : IIntegrationEvent;

/// <summary>
/// Public query interface: the tenant's configuration owner role, from its parking setting (ADR-022 decision 6), or the
/// default. Other alerts about missing or unusable configuration go to the same role (F009).
/// </summary>
public interface IConfigurationOwner
{
    Task<string> Role(Guid tenantId, CancellationToken cancellationToken);
}
