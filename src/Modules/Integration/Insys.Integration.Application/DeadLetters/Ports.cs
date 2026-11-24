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
using Insys.Integration.Domain.DeadLetters;

namespace Insys.Integration.Application.DeadLetters;

public interface IDeadLetterRepository
{
    Task<DeadLetter?> Get(Guid id, CancellationToken cancellationToken);

    void Add(DeadLetter deadLetter);
}

/// <summary>
/// DE-007: the delivery ledger's side of a discard. In the command's (or policy's) own transaction, the event's deliveries left DEAD_LETTERED
/// (integration.parked_delivery) become DISCARDED, so later events for the same records no longer wait behind them (ADR-022 decision 3).
/// </summary>
public interface IDeadLetterReleases
{
    /// <param name="source">The dead letter's source: a ledger-made one ("ledger:&lt;consumer&gt;") releases that consumer's delivery only.</param>
    /// <returns>How many deliveries were discarded.</returns>
    Task<int> DiscardDeliveries(Guid eventId, string source, string discardedBy, string reason, CancellationToken cancellationToken);
}

/// <param name="DiscardRequestedBy">DE-007: who asked for the discard of a money message that waits for a second person's approval (status DiscardRequested).</param>
public sealed record DeadLetterSummary(Guid Id, string Source, string MessageRef, DeadLetterStatus Status, short Attempts, DateTimeOffset FirstFailedAt,
    Guid? DiscardRequestedBy = null, DateTimeOffset? DiscardRequestedAt = null, string? DiscardReason = null);

/// <param name="DiscardReason">Why it was discarded, or why its discard was asked for (DE-007).</param>
/// <param name="DiscardApprovedBy">The second person who approved the discard of a money message (DE-007).</param>
public sealed record DeadLetterDetail(
    Guid Id, string Source, string MessageRef, DeadLetterStatus Status, short Attempts, DateTimeOffset FirstFailedAt, string Error,
    System.Text.Json.JsonElement Payload, Guid? ResolvedBy, long RowVersion, Guid? DiscardRequestedBy = null, DateTimeOffset? DiscardRequestedAt = null,
    string? DiscardReason = null, Guid? DiscardApprovedBy = null);

public interface IDeadLetterQueries
{
    Task<IReadOnlyList<DeadLetterSummary>> List(DeadLetterStatus? status, string? source, Guid? afterId, int limit, CancellationToken cancellationToken);

    Task<DeadLetterDetail?> Get(Guid id, CancellationToken cancellationToken);
}
