namespace Insys.BuildingBlocks.Contracts.Auditing;

/// <summary>
/// A sensitive operation and its outcome (IAM-006). Every module writes it to its own outbox in the command's
/// transaction (or, when the command fails, in a separate one), and the Audit module records it in
/// audit.audit_event. Details are masked before they get here (INT-007).
/// </summary>
[IntegrationEvent("audit.SensitiveOperationRecorded")]
public sealed record SensitiveOperationRecorded(
    string ActorId,
    string ActorType,
    string EventType,
    string Operation,
    string TargetType,
    string TargetId,
    string Outcome,
    DateTimeOffset OccurredAt,
    IReadOnlyDictionary<string, string?>? Details) : IIntegrationEvent;
