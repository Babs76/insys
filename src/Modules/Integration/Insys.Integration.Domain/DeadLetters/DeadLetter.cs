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

/// <summary>States from domain-model.json (integration › DeadLetter › stateTransitions).</summary>
public enum DeadLetterStatus
{
    DeadLettered,
    Replayed,
    Discarded,
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
        _ => DeadLetterStatus.DeadLettered,
    };

    /// <summary>The operator (iam.user_account) who replayed or discarded the message.</summary>
    public Guid? ResolvedBy { get; private set; }

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
    public Result ReplayMessage(Guid operatorId) => Resolve(DeadLetterTrigger.ReplayMessage, operatorId);

    /// <summary>Command DiscardMessage. Discarded only by an authorised operator (INT-006).</summary>
    public Result DiscardMessage(Guid operatorId) => Resolve(DeadLetterTrigger.DiscardMessage, operatorId);

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
