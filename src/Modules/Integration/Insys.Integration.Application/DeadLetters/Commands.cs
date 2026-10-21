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

/// <summary>Command DiscardMessage: closes the dead letter without delivery (INT-006).</summary>
public sealed record DiscardMessage(Guid DeadLetterId, long ExpectedVersion) : ICommand<long>, IVersionedCommand;

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
        return deadLetter.RowVersion + 1;
    }
}

internal sealed class DiscardMessageHandler(IDeadLetterRepository repository, IExecutionContext execution, IAuditTrail audit)
    : ICommandHandler<DiscardMessage, long>
{
    public async Task<Result<long>> Handle(DiscardMessage command, CancellationToken cancellationToken)
    {
        var loaded = await DeadLetterAccess.Load(repository, command.DeadLetterId, command.ExpectedVersion, execution, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var deadLetter = loaded.Value.DeadLetter;
        audit.Record(new AuditEntry("DEAD_LETTER", "DiscardMessage", nameof(DeadLetter), deadLetter.Id.ToString()));
        var resolved = deadLetter.DiscardMessage(loaded.Value.OperatorId);
        return resolved.IsFailure ? resolved.Error : deadLetter.RowVersion + 1;
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
