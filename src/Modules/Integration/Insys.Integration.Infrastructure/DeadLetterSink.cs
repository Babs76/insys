using System.Text.Json;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.Integration.Application.DeadLetters;

namespace Insys.Integration.Infrastructure;

/// <summary>Receives messages the outbox relay gave up on and records them as DeadLetter aggregates.</summary>
internal sealed class DeadLetterSink(IDispatcher dispatcher) : IDeadLetterSink
{
    public async Task DeadLetter(EventEnvelope envelope, string source, string error, short attempts, DateTimeOffset firstFailedAt, CancellationToken cancellationToken)
    {
        var result = await dispatcher.Send(new DeadLetterMessage(source, envelope.EventId.ToString(),
            JsonSerializer.Serialize(envelope, JsonSerializerOptions.Web), error, firstFailedAt, attempts), cancellationToken);
        if (result.IsFailure)
        {
            throw new InvalidOperationException($"Dead-lettering event {envelope.EventId} failed with {result.Error.Code}.");
        }
    }
}
