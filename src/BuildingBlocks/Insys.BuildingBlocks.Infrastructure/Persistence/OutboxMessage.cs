namespace Insys.BuildingBlocks.Infrastructure.Persistence;

/// <summary>A row in a module's &lt;schema&gt;.outbox_event table (DBA-006).</summary>
public sealed class OutboxMessage
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public long RowVersion { get; set; }

    public string AggregateType { get; set; } = "";

    public Guid AggregateId { get; set; }

    public string EventType { get; set; } = "";

    public short EventVersion { get; set; }

    public string Payload { get; set; } = "{}";

    public DateTimeOffset OccurredAt { get; set; }

    public Guid CorrelationId { get; set; }

    public Guid? CausationId { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }

    public short PublishAttempts { get; set; }
}
