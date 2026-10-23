/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Party
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
using Insys.BuildingBlocks.Domain;

namespace Insys.Party.Domain.Parties;

/// <summary>
/// A legal hold on the party search access log (party.search_log_hold, OQ-073): while it is active, the entries it covers
/// (one searcher's, or the whole log) are not deleted by retention. Placing and releasing it are audited.
/// </summary>
public sealed class SearchLogHold : AggregateRoot<Guid>
{
    private SearchLogHold()
    {
    }

    /// <summary>The searcher whose entries are held; null holds the tenant's whole log.</summary>
    public string? SearchedBy { get; private set; }

    public string Reason { get; private set; } = "";

    public string PlacedBy { get; private set; } = "";

    public DateTimeOffset PlacedAt { get; private set; }

    public string? ReleasedBy { get; private set; }

    public DateTimeOffset? ReleasedAt { get; private set; }

    public static SearchLogHold Place(string? searchedBy, string reason, string placedBy, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        SearchedBy = searchedBy is null ? null : Guard.MaxLength(searchedBy, 80),
        Reason = Guard.MaxLength(Guard.NotNullOrWhiteSpace(reason), 200),
        PlacedBy = Guard.MaxLength(placedBy, 80),
        PlacedAt = now,
    };

    public Result Release(string releasedBy, DateTimeOffset now)
    {
        if (ReleasedAt is not null)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "The hold is already released.");
        }

        ReleasedBy = Guard.MaxLength(releasedBy, 80);
        ReleasedAt = now;
        MarkChanged();
        return Result.Success();
    }
}
