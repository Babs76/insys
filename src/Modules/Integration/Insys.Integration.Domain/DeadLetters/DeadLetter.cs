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
using Insys.BuildingBlocks.Domain;

namespace Insys.Integration.Domain.DeadLetters;

/// <summary>
/// States from domain-model.json (integration › DeadLetter › stateTransitions), and DiscardRequested (DE-007, Ade 24 November 2026): a dead
/// letter whose discard waits for a second person's approval. DiscardRequested is derived, never stored, and is not in the lifecycle: the dead
/// letter is still dead-lettered underneath, with a request on it that the approval's ending applies or clears.
/// </summary>
public enum DeadLetterStatus
{
    DeadLettered,
    Replayed,
    Discarded,
    DiscardRequested,
}

/// <summary>How a dead letter was resolved; stored in the table's resolution column, empty while dead-lettered.</summary>
public enum DeadLetterResolution
{
    Replayed,
    Discarded,
}

public enum DeadLetterTrigger
{
    ReplayMessage,
    DiscardMessage,
}

/// <summary>Where the failed message came from, for example "outbox:party" (max 60 characters).</summary>
public sealed record Source
{
    public Source(string value) => Value = Guard.MaxLength(Guard.NotNullOrWhiteSpace(value), 60);

    public string Value { get; }
}

/// <summary>The failed message's ID in its source (max 80 characters).</summary>
public sealed record MessageRef
{
    public MessageRef(string value) => Value = Guard.MaxLength(Guard.NotNullOrWhiteSpace(value), 80);

    public string Value { get; }
}

/// <summary>Why delivery failed. Holds exception types only, never message text that could carry sensitive data.</summary>
public sealed record DeliveryError
{
    public DeliveryError(string value) => Value = Guard.NotNullOrWhiteSpace(value);

    public string Value { get; }
}

/// <summary>
/// A message that failed delivery after all retries (INT-006). Table integration.dead_letter_message.
/// Its payload is the original event envelope, which carries references only (INT-007).
/// </summary>
public sealed class DeadLetter : AggregateRoot<Guid>
{
    public static readonly StateMachine<DeadLetterStatus, DeadLetterTrigger> Lifecycle = new StateMachine<DeadLetterStatus, DeadLetterTrigger>()
        .Allow(DeadLetterStatus.DeadLettered, DeadLetterTrigger.ReplayMessage, DeadLetterStatus.Replayed)
        .Allow(DeadLetterStatus.DeadLettered, DeadLetterTrigger.DiscardMessage, DeadLetterStatus.Discarded);

    private DeadLetter()
    {
    }

    public Source Source { get; private set; } = null!;

    public MessageRef MessageRef { get; private set; } = null!;

    public string Payload { get; private set; } = "{}";

    public DeliveryError Error { get; private set; } = null!;

    public DateTimeOffset FirstFailedAt { get; private set; }

    public short Attempts { get; private set; }

    public DeadLetterResolution? Resolution { get; private set; }

    public DeadLetterStatus Status => Resolution switch
    {
        DeadLetterResolution.Replayed => DeadLetterStatus.Replayed,
        DeadLetterResolution.Discarded => DeadLetterStatus.Discarded,
        _ when DiscardRequestedBy is not null => DeadLetterStatus.DiscardRequested,
        _ => DeadLetterStatus.DeadLettered,
    };

    /// <summary>The operator (iam.user_account) who replayed or discarded the message; for an approved discard, the requester.</summary>
    public Guid? ResolvedBy { get; private set; }

    /// <summary>Why the message was discarded, or why its discard was requested (DE-007, max 300). Never the payload.</summary>
    public string? DiscardReason { get; private set; }

    /// <summary>The operator who asked for the discard of a money message (DE-007); null when no discard waits for approval.</summary>
    public Guid? DiscardRequestedBy { get; private set; }

    public DateTimeOffset? DiscardRequestedAt { get; private set; }

    /// <summary>What the discard's approval covers (<see cref="DiscardSubject"/>), returned with Workflow's decision.</summary>
    public string? DiscardSubjectHash { get; private set; }

    /// <summary>The second person who approved the discard (DE-007); null for a discard that needed no approval.</summary>
    public Guid? DiscardApprovedBy { get; private set; }

    /// <summary>Command DeadLetterMessage.</summary>
    public static DeadLetter DeadLetterMessage(Guid id, Source source, MessageRef messageRef, string payload, DeliveryError error,
        DateTimeOffset firstFailedAt, short attempts)
    {
        var deadLetter = new DeadLetter
        {
            Id = Guard.NotEmpty(id),
            Source = Guard.NotNull(source),
            MessageRef = Guard.NotNull(messageRef),
            Payload = Guard.NotNullOrWhiteSpace(payload),
            Error = Guard.NotNull(error),
            FirstFailedAt = firstFailedAt,
            Attempts = attempts,
        };

        // MessageDeadLettered (provenance P) is not raised: no context consumes it and no process needs it (PROMPT §5).
        return deadLetter;
    }

    /// <summary>Command ReplayMessage. Replayed only by an authorised operator (INT-006).</summary>
    public Result ReplayMessage(Guid operatorId) =>
        RefusedWhileDiscardRequested("replayed") is { } refused ? refused : Resolve(DeadLetterTrigger.ReplayMessage, operatorId);

    /// <summary>
    /// Command DiscardMessage. Discarded only by an authorised operator (INT-006), with a reason (DE-007). A message whose discard needs a
    /// second person (<see cref="DiscardApproval"/>) is not discarded here: <see cref="RequestDiscard"/> asks for the approval first.
    /// </summary>
    public Result DiscardMessage(Guid operatorId, string reason)
    {
        var refused = RefusedWhileDiscardRequested("discarded");
        if (refused is not null)
        {
            return refused;
        }

        if (string.IsNullOrWhiteSpace(reason) || reason.Length > MaxReasonLength)
        {
            return BuildingBlocks.Domain.Error.Validation(ErrorCodes.ValidationFailed, $"A discard needs a reason of at most {MaxReasonLength} characters (DE-007).");
        }

        var resolved = Resolve(DeadLetterTrigger.DiscardMessage, operatorId);
        if (resolved.IsSuccess)
        {
            DiscardReason = reason;
        }

        return resolved;
    }

    /// <summary>The longest discard reason (integration.dead_letter_message.discard_reason).</summary>
    public const int MaxReasonLength = 300;

    /// <summary>
    /// DE-007: asks a second person to approve discarding a money message. The dead letter stays dead-lettered, with the request on it
    /// (DiscardRequested), until Workflow's decision; while it waits, it is neither replayed nor asked to be discarded again.
    /// </summary>
    public Result RequestDiscard(Guid operatorId, string reason, DateTimeOffset now)
    {
        if (operatorId == Guid.Empty)
        {
            return BuildingBlocks.Domain.Error.Forbidden(ErrorCodes.Forbidden, "Dead letters are resolved only by an identified operator (INT-006).");
        }

        var refused = RefusedWhileDiscardRequested("asked to be discarded again");
        if (refused is not null)
        {
            return refused;
        }

        if (string.IsNullOrWhiteSpace(reason) || reason.Length > MaxReasonLength)
        {
            return BuildingBlocks.Domain.Error.Validation(ErrorCodes.ValidationFailed, $"A discard needs a reason of at most {MaxReasonLength} characters (DE-007).");
        }

        // Only a dead letter that could be discarded now can be asked to be: a replayed or discarded one is refused here.
        var next = Lifecycle.Fire(Status, DeadLetterTrigger.DiscardMessage);
        if (next.IsFailure)
        {
            return next.Error;
        }

        DiscardRequestedBy = operatorId;
        DiscardRequestedAt = now;
        DiscardReason = reason;
        DiscardSubjectHash = DiscardSubject(Id, MessageRef.Value, reason, now);
        Raise(new DiscardRequested(Id, operatorId, reason, DiscardSubjectHash, now));
        MarkChanged();
        return Result.Success();
    }

    /// <summary>What a discard approval covers: this dead letter, its message, the reason and when it was asked, so each request has its own subject.</summary>
    public static string DiscardSubject(Guid id, string messageRef, string reason, DateTimeOffset requestedAt) =>
        ApprovalSubject.Hash("DEAD_LETTER_DISCARD", id, messageRef, reason, requestedAt.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>True when this checker's approval of the discard was already applied: a redelivered grant changes nothing.</summary>
    public bool DiscardGrantedBy(Guid checkerId) => Status == DeadLetterStatus.Discarded && DiscardApprovedBy == checkerId;

    /// <summary>
    /// Workflow granted the discard (DE-007): the message is discarded, resolved by the requester, with the approver recorded. The grant applies only to
    /// the request it was for (its subject hash), and never from the requester.
    /// </summary>
    public Result ApproveDiscard(Guid checkerId, string? subjectHash)
    {
        if (Status != DeadLetterStatus.DiscardRequested || subjectHash != DiscardSubjectHash)
        {
            return BuildingBlocks.Domain.Error.Conflict(ErrorCodes.InvalidStateTransition, "No discard of this dead letter waits for that approval.");
        }

        var requester = DiscardRequestedBy!.Value;
        if (checkerId == requester)
        {
            return MakerChecker.EnsureDifferentPeople(requester.ToString(), checkerId.ToString());
        }

        var next = Lifecycle.Fire(DeadLetterStatus.DeadLettered, DeadLetterTrigger.DiscardMessage);
        if (next.IsFailure)
        {
            return next.Error;
        }

        Resolution = DeadLetterResolution.Discarded;
        ResolvedBy = requester;
        DiscardApprovedBy = checkerId;
        MarkChanged();
        return Result.Success();
    }

    /// <summary>
    /// The discard's approval ended without being granted (rejected, expired or withdrawn): the request is cleared and the dead letter stays
    /// dead-lettered. A dead letter with no request waiting takes a redelivered ending unchanged; one waiting on another request refuses it.
    /// </summary>
    public Result ClearDiscardRequest(Guid checkerId, string? subjectHash)
    {
        if (Status != DeadLetterStatus.DiscardRequested)
        {
            return Result.Success();
        }

        if (subjectHash != DiscardSubjectHash)
        {
            return BuildingBlocks.Domain.Error.Conflict(ErrorCodes.InvalidStateTransition, "The approval's ending was for another discard request.");
        }

        if (checkerId != Guid.Empty && checkerId == DiscardRequestedBy)
        {
            return MakerChecker.EnsureDifferentPeople(DiscardRequestedBy.Value.ToString(), checkerId.ToString());
        }

        DiscardRequestedBy = null;
        DiscardRequestedAt = null;
        DiscardReason = null;
        DiscardSubjectHash = null;
        MarkChanged();
        return Result.Success();
    }

    private BuildingBlocks.Domain.Error? RefusedWhileDiscardRequested(string what) =>
        Status == DeadLetterStatus.DiscardRequested
            ? BuildingBlocks.Domain.Error.Conflict(ErrorCodes.InvalidStateTransition,
                $"A discard of this dead letter is waiting for a second person's approval, so it cannot be {what} until that approval ends (DE-007).")
            : null;

    private Result Resolve(DeadLetterTrigger trigger, Guid operatorId)
    {
        if (operatorId == Guid.Empty)
        {
            return BuildingBlocks.Domain.Error.Forbidden(ErrorCodes.Forbidden, "Dead letters are resolved only by an identified operator (INT-006).");
        }

        var next = Lifecycle.Fire(Status, trigger);
        if (next.IsFailure)
        {
            return next.Error;
        }

        Resolution = next.Value == DeadLetterStatus.Replayed ? DeadLetterResolution.Replayed : DeadLetterResolution.Discarded;
        ResolvedBy = operatorId;
        MarkChanged();
        return Result.Success();
    }
}

/// <summary>DE-007: a money message's discard asks Workflow for a second person's approval (DEAD_LETTER_DISCARD). Mapped to workflow.ApprovalRequested.</summary>
public sealed record DiscardRequested(Guid DeadLetterId, Guid RequestedBy, string Reason, string SubjectHash, DateTimeOffset OccurredAt) : IDomainEvent;
