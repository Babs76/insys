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
using System.Text.Json;
using FluentValidation;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
using Insys.Integration.Domain.DeadLetters;

namespace Insys.Integration.Application.DeadLetters;

/// <summary>Command DeadLetterMessage: records a message whose delivery failed after all retries.</summary>
public sealed record DeadLetterMessage(string Source, string MessageRef, string Payload, string Error, DateTimeOffset FirstFailedAt, short Attempts)
    : ICommand<Guid>;

/// <summary>Command ReplayMessage: delivers the message again to its consumers (INT-006).</summary>
public sealed record ReplayMessage(Guid DeadLetterId, long ExpectedVersion) : ICommand<long>, IVersionedCommand;

/// <summary>
/// Command DiscardMessage: closes the dead letter without delivery (INT-006), and releases what it held back (DE-007): its event's dead-lettered
/// deliveries in the ledger are discarded, so the events waiting behind them go on. <paramref name="MessageRef"/> is typed by the operator and must
/// be the dead letter's message ID exactly. A money message's discard asks for a second person's approval first (<see cref="DiscardApproval"/>).
/// </summary>
public sealed record DiscardMessage(Guid DeadLetterId, long ExpectedVersion, string MessageRef, string Reason) : ICommand<DiscardOutcome>, IVersionedCommand;

/// <summary>What a discard did: the dead letter was discarded, or (a money message) its discard now waits for a second person's approval.</summary>
public sealed record DiscardOutcome(Guid Id, bool AwaitingApproval)
{
    /// <summary>The dead letter's version once saved (its ETag).</summary>
    public long RowVersion { get; set; }
}

public sealed class DiscardMessageValidator : AbstractValidator<DiscardMessage>
{
    public DiscardMessageValidator()
    {
        RuleFor(c => c.MessageRef).NotEmpty().MaximumLength(80);
        RuleFor(c => c.Reason).NotEmpty().MaximumLength(DeadLetter.MaxReasonLength);
    }
}

public sealed class DeadLetterMessageValidator : AbstractValidator<DeadLetterMessage>
{
    public DeadLetterMessageValidator()
    {
        RuleFor(c => c.Source).NotEmpty().MaximumLength(60);
        RuleFor(c => c.MessageRef).NotEmpty().MaximumLength(80);
        RuleFor(c => c.Payload).NotEmpty();
        RuleFor(c => c.Error).NotEmpty();
        RuleFor(c => c.Attempts).GreaterThan((short)0);
    }
}

internal sealed class DeadLetterMessageHandler(IDeadLetterRepository repository) : ICommandHandler<DeadLetterMessage, Guid>
{
    public Task<Result<Guid>> Handle(DeadLetterMessage command, CancellationToken cancellationToken)
    {
        var deadLetter = DeadLetter.DeadLetterMessage(Guid.CreateVersion7(), new Source(command.Source), new MessageRef(command.MessageRef),
            command.Payload, new DeliveryError(command.Error), command.FirstFailedAt, command.Attempts);
        repository.Add(deadLetter);
        return Task.FromResult<Result<Guid>>(deadLetter.Id);
    }
}

internal sealed class ReplayMessageHandler(IDeadLetterRepository repository, IEventBus bus, IExecutionContext execution, IAuditTrail audit)
    : ICommandHandler<ReplayMessage, long>
{
    public async Task<Result<long>> Handle(ReplayMessage command, CancellationToken cancellationToken)
    {
        var loaded = await DeadLetterAccess.Load(repository, command.DeadLetterId, command.ExpectedVersion, execution, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var deadLetter = loaded.Value.DeadLetter;

        // A failed webhook's payload is the webhook body, not an event envelope, and a failed delivery cannot become
        // pending again (domain-model.json: Pending → Delivered or Failed only). TODO(spec): resending it (OQ-066).
        if (deadLetter.Source.Value.StartsWith("webhook:", StringComparison.Ordinal))
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition,
                "A failed webhook delivery cannot be replayed; discard it, or ask the partner to fetch the event (OQ-066).");
        }

        audit.Record(new AuditEntry("DEAD_LETTER", "ReplayMessage", nameof(DeadLetter), deadLetter.Id.ToString()));
        var resolved = deadLetter.ReplayMessage(loaded.Value.OperatorId);
        if (resolved.IsFailure)
        {
            return resolved.Error;
        }

        // Consumers are idempotent through the inbox (INT-005), so delivering before the commit is safe: if the commit
        // fails, a later replay is skipped by the consumers that already processed the event.
        var envelope = JsonSerializer.Deserialize<EventEnvelope>(deadLetter.Payload, JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException("The dead letter's payload is not an event envelope.");
        await bus.Publish(envelope, cancellationToken);
        return deadLetter.RowVersion;
    }
}

internal sealed class DiscardMessageHandler(IDeadLetterRepository repository, IDeadLetterReleases releases, IExecutionContext execution, IAuditTrail audit,
    IClock clock) : ICommandHandler<DiscardMessage, DiscardOutcome>
{
    public async Task<Result<DiscardOutcome>> Handle(DiscardMessage command, CancellationToken cancellationToken)
    {
        var loaded = await DeadLetterAccess.Load(repository, command.DeadLetterId, command.ExpectedVersion, execution, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var (deadLetter, operatorId) = loaded.Value;

        // DE-007: the operator types the message ID as a deliberate confirmation; anything else changes nothing.
        if (!string.Equals(command.MessageRef, deadLetter.MessageRef.Value, StringComparison.Ordinal))
        {
            return Error.Validation(ErrorCodes.TypedReferenceMismatch, "The message reference typed is not this dead letter's message ID, so nothing was discarded.");
        }

        var held = HeldEvent.Of(deadLetter);
        if (DiscardApproval.NeedsSecondPerson(deadLetter.Source.Value, held?.EventType))
        {
            var requested = deadLetter.RequestDiscard(operatorId, command.Reason, clock.UtcNow);
            if (requested.IsFailure)
            {
                return requested.Error;
            }

            audit.Record(new AuditEntry("DEAD_LETTER", "RequestDiscardMessage", nameof(DeadLetter), deadLetter.Id.ToString(),
                new Dictionary<string, string?> { ["source"] = deadLetter.Source.Value, ["eventType"] = held?.EventType, ["reason"] = command.Reason }));
            return new DiscardOutcome(deadLetter.Id, AwaitingApproval: true) { RowVersion = deadLetter.RowVersion };
        }

        var discarded = deadLetter.DiscardMessage(operatorId, command.Reason);
        if (discarded.IsFailure)
        {
            return discarded.Error;
        }

        await DeadLetterDiscards.Release(deadLetter, held, releases, audit, approvedBy: null, cancellationToken);
        return new DiscardOutcome(deadLetter.Id, AwaitingApproval: false) { RowVersion = deadLetter.RowVersion };
    }
}

/// <summary>The event a dead letter holds back: an outbox's or the ledger's dead letter holds its event envelope; a webhook's holds none.</summary>
public sealed record HeldEvent(Guid EventId, string EventType)
{
    public static HeldEvent? Of(DeadLetter deadLetter)
    {
        if (!deadLetter.Source.Value.StartsWith("outbox:", StringComparison.Ordinal) && !deadLetter.Source.Value.StartsWith("ledger:", StringComparison.Ordinal))
        {
            return null;
        }

        var envelope = JsonSerializer.Deserialize<EventEnvelope>(deadLetter.Payload, JsonSerializerOptions.Web);
        return envelope is null || envelope.EventId == Guid.Empty ? null : new HeldEvent(envelope.EventId, envelope.EventType);
    }
}

/// <summary>DE-007: what a discard does once it takes effect, directly or once approved: releases the event's dead-lettered deliveries and audits it.</summary>
public static class DeadLetterDiscards
{
    public static async Task Release(DeadLetter deadLetter, HeldEvent? held, IDeadLetterReleases releases, IAuditTrail audit, Guid? approvedBy,
        CancellationToken cancellationToken)
    {
        var released = held is null ? 0
            : await releases.DiscardDeliveries(held.EventId, deadLetter.Source.Value, deadLetter.ResolvedBy!.Value.ToString(), deadLetter.DiscardReason!, cancellationToken);

        // The reason and what was released; never the payload (INT-007, BR-017).
        audit.Record(new AuditEntry("DEAD_LETTER", "DiscardMessage", nameof(DeadLetter), deadLetter.Id.ToString(),
            new Dictionary<string, string?>
            {
                ["source"] = deadLetter.Source.Value,
                ["eventType"] = held?.EventType,
                ["reason"] = deadLetter.DiscardReason,
                ["requestedBy"] = deadLetter.ResolvedBy?.ToString(),
                ["approvedBy"] = approvedBy?.ToString(),
                ["releasedDeliveries"] = released.ToString(System.Globalization.CultureInfo.InvariantCulture),
            }));
    }
}

internal static class DeadLetterAccess
{
    public static async Task<Result<(DeadLetter DeadLetter, Guid OperatorId)>> Load(
        IDeadLetterRepository repository, Guid id, long expectedVersion, IExecutionContext execution, CancellationToken cancellationToken)
    {
        // INT-006: an operator is a user whose subject is an iam.user_account ID. TODO(spec): IdP subject mapping (SRS §30).
        if (execution.ActorType != ActorType.User || !Guid.TryParse(execution.ActorId, out var operatorId))
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Dead letters are resolved only by an identified operator (INT-006).");
        }

        var deadLetter = await repository.Get(id, cancellationToken);
        if (deadLetter is null)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The dead letter was not found.");
        }

        if (deadLetter.RowVersion != expectedVersion)
        {
            return Error.PreconditionFailed(ErrorCodes.PreconditionFailed, "The dead letter has changed since it was read.");
        }

        return (deadLetter, operatorId);
    }
}
