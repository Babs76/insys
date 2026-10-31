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
namespace Insys.BuildingBlocks.Application.Context;

/// <summary>
/// A module's record linked to a party, for the anonymisation check (D-34, OQ-120): its retention runs from
/// <see cref="ClosedOn"/>, the business date it reached its final status (from its status history), for the tenant's
/// period of its <see cref="Retention"/> kind. Null while it is not in a final status, so it keeps the party's data.
/// </summary>
/// <param name="RecordType">LIFE_POLICY, LIFE_CLAIM, GENERAL_POLICY, GENERAL_CLAIM, HEALTH_MEMBERSHIP, HEALTH_CLAIM, CASE or PAYMENT.</param>
/// <param name="Retention">Which retention period applies: <see cref="PartyRecordRetention"/>.</param>
public sealed record PartyLinkedRecord(string RecordType, Guid RecordId, string Retention, DateOnly? ClosedOn);

/// <summary>The record kinds of party.cfg_record_retention_policies.</summary>
public static class PartyRecordRetention
{
    /// <summary>A policy or membership, from its final status (ended).</summary>
    public const string Policy = "POLICY";

    /// <summary>A claim, from its final status (closed or decided).</summary>
    public const string Claim = "CLAIM";

    /// <summary>A payment, from its final status (an outcome).</summary>
    public const string Payment = "PAYMENT";

    /// <summary>A case, from its closing or withdrawal.</summary>
    public const string Case = "CASE";
}

/// <summary>
/// Public query interface, implemented by each module that keeps records about a party (D-34): its records linked to the
/// party, read in the tenant on the module's own role. <paramref name="timeZone"/> is the tenant's IANA zone, for the
/// business date of a closure recorded as an instant.
/// </summary>
public interface IPartyLinkedRecords
{
    Task<IReadOnlyList<PartyLinkedRecord>> For(Guid tenantId, Guid partyId, string timeZone, CancellationToken cancellationToken);
}
