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

    /// <summary>The LEGAL_HOLD_RELEASE approval that released the hold.</summary>
    public Guid? ReleaseApprovalId { get; private set; }

    /// <summary>Who asked for the hold to be released, while the release waits for approval (OQ-111).</summary>
    public Guid? ReleaseRequestedBy { get; private set; }

    public DateTimeOffset? ReleaseRequestedAt { get; private set; }

    public string? ReleaseReason { get; private set; }

    public bool IsActive => ReleasedAt is null;

    /// <summary>A release was asked for and waits for a second person's approval; the hold stays in force meanwhile.</summary>
    public bool ReleasePending => IsActive && ReleaseRequestedBy is not null;

    public static SearchLogHold Place(string? searchedBy, string reason, string placedBy, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        SearchedBy = searchedBy is null ? null : Guard.MaxLength(searchedBy, 80),
        Reason = Guard.MaxLength(Guard.NotNullOrWhiteSpace(reason), 200),
        PlacedBy = Guard.MaxLength(placedBy, 80),
        PlacedAt = now,
    };

    /// <summary>
    /// Command RequestSearchLogHoldRelease (OQ-111, 25 October 2026): releasing a hold needs a second person, so no one
    /// person can release a hold and have the entries it protected deleted. The request is recorded (who, when, why) and
    /// asks Workflow for LEGAL_HOLD_RELEASE approval; the hold stays in force until the release is approved.
    /// </summary>
    public Result RequestRelease(Guid requestedBy, string reason, DateTimeOffset now)
    {
        if (!IsActive)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "The hold is already released.");
        }

        if (ReleasePending)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "Releasing the hold is already waiting for approval.");
        }

        ReleaseRequestedBy = requestedBy;
        ReleaseRequestedAt = now;
        ReleaseReason = Guard.MaxLength(Guard.NotNullOrWhiteSpace(reason), 200);
        Raise(new SearchLogHoldReleaseRequested(Id, SearchedBy, requestedBy, ReleaseReason, now));
        MarkChanged();
        return Result.Success();
    }

    /// <summary>True when the granted release was already applied: the hold was released by that approval. A redelivered grant changes nothing.</summary>
    public bool ReleaseApplied(Guid approvalId) => !IsActive && ReleaseApprovalId == approvalId;

    /// <summary>
    /// Workflow's decision on a release request. Approved: the hold is released, and retention may then delete the entries
    /// it kept. Every other ending (rejected, expired, withdrawn) clears the request and the hold stays in force; with no
    /// request waiting, such an ending was already applied. A grant with no request waiting (the hold changed: released, or
    /// the request gone) is refused, as is a checker who is the requester (SRS-002).
    /// </summary>
    public Result DecideRelease(bool approved, Guid approvalId, Guid checkerId, DateTimeOffset now)
    {
        if (!ReleasePending || ReleaseRequestedBy is not { } requester)
        {
            return approved ? Error.Conflict(ErrorCodes.InvalidStateTransition, "No release of the hold is waiting for approval.") : Result.Success();
        }

        if ((approved || checkerId != Guid.Empty) && checkerId == requester)
        {
            return MakerChecker.EnsureDifferentPeople(requester.ToString(), checkerId.ToString());
        }

        ReleaseRequestedBy = null;
        ReleaseRequestedAt = null;
        ReleaseReason = null;
        if (approved)
        {
            ReleasedBy = checkerId.ToString();
            ReleasedAt = now;
            ReleaseApprovalId = approvalId;
        }

        MarkChanged();
        return Result.Success();
    }
}

/// <summary>Releasing a search log hold asks Workflow for a second person's approval (LEGAL_HOLD_RELEASE, OQ-111).</summary>
public sealed record SearchLogHoldReleaseRequested(Guid HoldId, string? SearchedBy, Guid RequestedBy, string Reason, DateTimeOffset OccurredAt) : IDomainEvent;
