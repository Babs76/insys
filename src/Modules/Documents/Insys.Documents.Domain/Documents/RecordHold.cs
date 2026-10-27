/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Documents
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

namespace Insys.Documents.Domain.Documents;

/// <summary>The records a legal hold can be placed on itself (D-34, 27 October 2026). Payments are held through their policy or claim.</summary>
public static class RecordHoldScopes
{
    public const string Party = "PARTY";
    public const string LifePolicy = "LIFE_POLICY";
    public const string GeneralPolicy = "GENERAL_POLICY";
    public const string HealthMembership = "HEALTH_MEMBERSHIP";
    public const string LifeClaim = "LIFE_CLAIM";
    public const string GeneralClaim = "GENERAL_CLAIM";
    public const string HealthClaim = "HEALTH_CLAIM";
    public const string Case = "CASE";

    public static readonly IReadOnlyList<string> All = [Party, LifePolicy, GeneralPolicy, HealthMembership, LifeClaim, GeneralClaim, HealthClaim, Case];
}

/// <summary>Releasing a record's hold asks Workflow for a second person's approval (LEGAL_HOLD_RELEASE, OQ-111).</summary>
public sealed record RecordHoldReleaseRequested(Guid HoldId, string ScopeType, Guid ScopeId, string ReasonCode, Guid RequestedBy, string Reason, DateTimeOffset OccurredAt)
    : IDomainEvent;

/// <summary>
/// Aggregate RecordHold (document.record_hold, D-34): a legal hold on a record itself, not on one of its documents. While
/// it is in force, the record's documents are not disposed of, a held case is not disposed of, and the party it is on, or
/// whose linked record it is on, is not anonymised. Like a document's hold, it is released only when a second person
/// approves the release (OQ-111); until then it stays in force.
/// </summary>
public sealed class RecordHold : AggregateRoot<Guid>
{
    private RecordHold()
    {
    }

    public string ScopeType { get; private set; } = "";

    public Guid ScopeId { get; private set; }

    public string ReasonCode { get; private set; } = "";

    public Guid PlacedBy { get; private set; }

    public DateTimeOffset PlacedAt { get; private set; }

    public DateTimeOffset? ReleasedAt { get; private set; }

    /// <summary>The checker who approved the release.</summary>
    public Guid? ReleasedBy { get; private set; }

    /// <summary>The LEGAL_HOLD_RELEASE approval that released the hold.</summary>
    public Guid? ReleaseApprovalId { get; private set; }

    public Guid? ReleaseRequestedBy { get; private set; }

    public DateTimeOffset? ReleaseRequestedAt { get; private set; }

    public string? ReleaseReason { get; private set; }

    public bool IsActive => ReleasedAt is null;

    /// <summary>A release was asked for and waits for a second person's approval; the hold stays in force meanwhile.</summary>
    public bool ReleasePending => IsActive && ReleaseRequestedBy is not null;

    /// <summary>Command PlaceRecordHold. The caller has checked that the record exists and is in the caller's scope.</summary>
    public static Result<RecordHold> Place(string scopeType, Guid scopeId, string reasonCode, Guid placedBy, DateTimeOffset now)
    {
        if (!RecordHoldScopes.All.Contains(scopeType))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, $"A legal hold is placed on one of: {string.Join(", ", RecordHoldScopes.All)}.");
        }

        return new RecordHold
        {
            Id = Guid.CreateVersion7(),
            ScopeType = scopeType,
            ScopeId = scopeId,
            ReasonCode = Guard.MaxLength(Guard.NotNullOrWhiteSpace(reasonCode), 15),
            PlacedBy = placedBy,
            PlacedAt = now,
        };
    }

    /// <summary>Command RequestRecordHoldRelease (OQ-111): recorded on the hold, which stays in force until a second person approves.</summary>
    public Result RequestRelease(Guid requestedBy, string reason, DateTimeOffset now)
    {
        if (!IsActive)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "The hold was released.");
        }

        if (ReleasePending)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "Releasing the hold is already waiting for approval.");
        }

        var why = Guard.MaxLength(Guard.NotNullOrWhiteSpace(reason), 200);
        ReleaseRequestedBy = requestedBy;
        ReleaseRequestedAt = now;
        ReleaseReason = why;
        Raise(new RecordHoldReleaseRequested(Id, ScopeType, ScopeId, ReasonCode, requestedBy, why, now));
        MarkChanged();
        return Result.Success();
    }

    /// <summary>True when the granted release was already applied by that approval: a redelivered grant changes nothing.</summary>
    public bool ReleaseApplied(Guid approvalId) => !IsActive && ReleaseApprovalId == approvalId;

    /// <summary>
    /// Workflow's decision on a release request, as for a document's hold: approved, the hold is released; any other
    /// ending clears the request and the hold stays in force. A grant with no request waiting, or from the requester, is refused.
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
            ReleasedAt = now;
            ReleasedBy = checkerId;
            ReleaseApprovalId = approvalId;
        }

        MarkChanged();
        return Result.Success();
    }
}
