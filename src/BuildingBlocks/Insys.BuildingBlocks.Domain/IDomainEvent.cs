namespace Insys.BuildingBlocks.Domain;

/// <summary>
/// Something that happened in an aggregate. Class names match the event names in domain-model.json exactly.
/// Payloads carry references, never sensitive values (INT-007).
/// </summary>
public interface IDomainEvent
{
    DateTimeOffset OccurredAt { get; }
}
