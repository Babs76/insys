/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Contracts
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
namespace Insys.BuildingBlocks.Contracts;

/// <summary>
/// An event published to other modules. It lives in the publishing module's Contracts project and carries
/// references, not sensitive values (INT-007).
/// </summary>
public interface IIntegrationEvent;

/// <summary>
/// Names an integration event. <see cref="Type"/> is "{context}.{EventName}", using the context ID and event name
/// from domain-model.json. The context prefix is needed because some event names occur in two contexts
/// (for example ReconciliationExceptionRaised).
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class IntegrationEventAttribute(string type, short version = 1) : Attribute
{
    public string Type { get; } = type;

    public short Version { get; } = version;
}

/// <summary>
/// Any published event, as its JSON payload, for a consumer that subscribes to every event type (partner webhooks). Its
/// envelope says which event it is.
/// </summary>
public sealed record AnyIntegrationEvent(System.Text.Json.JsonElement Payload) : IIntegrationEvent
{
    /// <summary>The subscription event type that receives every event.</summary>
    public const string Wildcard = "*";
}
