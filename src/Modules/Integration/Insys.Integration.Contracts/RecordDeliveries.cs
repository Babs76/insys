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
namespace Insys.Integration.Contracts;

/// <summary>
/// The open delivery of a record's event to one consumer, from the delivery ledger (ADR-022): PARKED, WAITING, FAILING,
/// RETRYING or DEAD_LETTERED, with the parking code and item (configuration names and codes only, never a party).
/// </summary>
public sealed record RecordDelivery(string Status, string? Code, string? Item);

/// <summary>
/// Public query interface: a record's open delivery to one consumer, and an operator's retry of it. Used by a module that
/// shows the state of work it handed to another delivery (BR-018: a tenant administrator's Keycloak identity).
/// </summary>
public interface IRecordDeliveries
{
    /// <summary>In <paramref name="tenantId"/>: the record's most recent open delivery to the consumer, or null when none is open.</summary>
    Task<RecordDelivery?> Open(Guid tenantId, string consumer, string aggregateType, Guid aggregateId, CancellationToken cancellationToken);

    /// <summary>
    /// In <paramref name="tenantId"/>: retries the record's parked or dead-lettered deliveries to the consumer, now (the
    /// parking job takes them on its next run). Returns how many were re-armed.
    /// </summary>
    Task<int> Retry(Guid tenantId, string consumer, string aggregateType, Guid aggregateId, CancellationToken cancellationToken);
}
