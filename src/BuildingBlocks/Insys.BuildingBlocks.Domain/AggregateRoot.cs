/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Domain
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
namespace Insys.BuildingBlocks.Domain;

/// <summary>
/// The consistency boundary. A command changes one aggregate in one transaction and the aggregate's
/// events are written to the module outbox in that transaction (§23.2, DBA-006).
/// </summary>
public abstract class AggregateRoot<TId> : Entity<TId>, IAggregateRoot, ITenantScoped
    where TId : notnull
{
    private readonly List<IDomainEvent> _domainEvents = [];

    /// <summary>Set by the infrastructure on insert; an aggregate that is itself a tenant (the Tenant register) sets it to its own ID.</summary>
    public Guid TenantId { get; protected set; }

    /// <summary>Optimistic concurrency token mapped to <c>row_version</c> (DBA-011).</summary>
    public long RowVersion { get; private set; }

    public bool HasChanges { get; private set; }

    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents;

    protected void Raise(IDomainEvent domainEvent)
    {
        _domainEvents.Add(domainEvent);
        HasChanges = true;
    }

    /// <summary>For an aggregate a repository rebuilds from a row it does not map directly (a static data row, for example).</summary>
    protected void RestoreRowVersion(long rowVersion) => RowVersion = rowVersion;

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
