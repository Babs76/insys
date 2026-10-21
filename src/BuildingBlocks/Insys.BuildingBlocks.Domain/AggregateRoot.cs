namespace Insys.BuildingBlocks.Domain;

/// <summary>
/// The consistency boundary. A command changes one aggregate in one transaction and the aggregate's
/// events are written to the module outbox in that transaction (§23.2, DBA-006).
/// </summary>
public abstract class AggregateRoot<TId> : Entity<TId>, IAggregateRoot, ITenantScoped
    where TId : notnull
{
    private readonly List<IDomainEvent> _domainEvents = [];

    public Guid TenantId { get; private set; }

    /// <summary>Optimistic concurrency token mapped to <c>row_version</c> (DBA-011).</summary>
    public long RowVersion { get; private set; }

    public bool HasChanges { get; private set; }

    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents;

    protected void Raise(IDomainEvent domainEvent)
    {
        _domainEvents.Add(domainEvent);
        HasChanges = true;
    }

    /// <summary>Records that state changed without an event, so the version still moves.</summary>
    protected void MarkChanged() => HasChanges = true;

    IReadOnlyList<IDomainEvent> IAggregateRoot.DequeueDomainEvents()
    {
        var events = _domainEvents.ToArray();
        _domainEvents.Clear();
        return events;
    }

    void IAggregateRoot.AcceptChanges() => HasChanges = false;

    object IAggregateRoot.Id => Id;
}

/// <summary>Non-generic view used by the unit of work.</summary>
public interface IAggregateRoot
{
    object Id { get; }
    Guid TenantId { get; }
    long RowVersion { get; }
    bool HasChanges { get; }
    IReadOnlyList<IDomainEvent> DomainEvents { get; }
    IReadOnlyList<IDomainEvent> DequeueDomainEvents();
    void AcceptChanges();
}
