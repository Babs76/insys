using System.Text.Json;

namespace Insys.BuildingBlocks.Contracts;

/// <summary>The metadata every event carries (SRS §23.2).</summary>
public sealed record EventEnvelope(
    Guid EventId,
    Guid TenantId,
    string EventType,
    short EventVersion,
    DateTimeOffset OccurredAt,
    string AggregateType,
    Guid AggregateId,
    Guid CorrelationId,
    Guid? CausationId,
    JsonElement Payload);
