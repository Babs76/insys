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
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
using Insys.Integration.Application.DeadLetters;
using Insys.Integration.Domain.DeadLetters;
using Insys.Integration.Infrastructure.Persistence;
using Insys.Workflow.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Insys.Integration.Infrastructure;

/// <summary>
/// DE-007: a discard's ledger side, in the Integration context's own transaction (the command's or the policy's), so the dead letter and the
/// deliveries it held back change together. Discarded deliveries no longer block (ParkedRows.BlockingStatuses), and the parking job releases
/// the events waiting behind them in order (ParkingJob.UnblockedRecords, then ClaimNextWaiting).
/// </summary>
internal sealed class DeadLetterReleases(IntegrationDbContext db, ITenantContext tenant, IClock clock) : IDeadLetterReleases
{
    public async Task<int> DiscardDeliveries(Guid eventId, string source, string discardedBy, string reason, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var now = clock.UtcNow;
        var by = discardedBy.Length <= 80 ? discardedBy : discardedBy[..80];

        // A ledger-made dead letter is one consumer's ("ledger:<consumer>", cut to the source's 60 characters); an outbox's covers every consumer
        // the outbox gave up on.
        string? ledgerSource = source.StartsWith("ledger:", StringComparison.Ordinal) ? source : null;
        var discarded = await db.Database.SqlQuery<DiscardedDelivery>($"""
            UPDATE integration.parked_delivery
            SET status = 'DISCARDED', discarded_by = {by}, discard_reason = {reason}, resolved_at = {now},
                row_version = row_version + 1, updated_at = {now}, updated_by = {by}
            WHERE tenant_id = {tenantId} AND event_id = {eventId} AND status = 'DEAD_LETTERED'
              AND ({ledgerSource}::text IS NULL OR left('ledger:' || consumer, 60) = {ledgerSource})
            RETURNING id AS "Id", consumer AS "Consumer", prerequisite_code AS "Code", prerequisite_item AS "Item", event_type AS "EventType", event_id AS "EventId"
            """).ToListAsync(cancellationToken);

        // DE-028: Workflow closes each discarded delivery's tasks, in the same transaction.
        foreach (var d in discarded)
        {
            db.Publish(new Contracts.ParkedDeliveryDiscarded(d.Id, d.Consumer, d.Code, d.Item, d.EventType, d.EventId), "ParkedDelivery", d.Id, now);
        }

        return discarded.Count;
    }

    private sealed class DiscardedDelivery
    {
        public Guid Id { get; set; }

        public string Consumer { get; set; } = "";

        public string? Code { get; set; }

        public string? Item { get; set; }

        public string EventType { get; set; } = "";

        public Guid EventId { get; set; }
    }
}

/// <summary>Integration's domain events on its outbox: a money dead letter's discard asks Workflow for a second person's approval (DE-007).</summary>
internal sealed class IntegrationEventMapper : IDomainEventMapper
{
    public IIntegrationEvent? Map(IDomainEvent domainEvent) => domainEvent switch
    {
        DiscardRequested e => new BuildingBlocks.Contracts.Approvals.ApprovalRequested(DiscardApproval.BusinessRefType, e.DeadLetterId, DiscardApproval.ApprovalType,
            e.RequestedBy, null, null, e.Reason, e.SubjectHash),
        _ => null,
    };
}

/// <summary>
/// Workflow's decision on a money dead letter's discard (DE-007). Granted: the dead letter is discarded as the requester asked, with the approver
/// recorded, and what it held back is released. Every other ending (rejected, expired, withdrawn): the request is cleared and the dead letter stays
/// dead-lettered. A decision the dead letter will not take (another request, already resolved otherwise) parks the delivery (ADR-022, D-5).
/// </summary>
public sealed class DeadLetterDiscardPolicies(IDeadLetterRepository repository, IDeadLetterReleases releases, BuildingBlocks.Application.Auditing.IAuditTrail audit) :
    IIntegrationEventHandler<ApprovalGranted>,
    IIntegrationEventHandler<ApprovalRejected>
{
    public async Task Handle(ApprovalGranted granted, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (granted.BusinessRefType != DiscardApproval.BusinessRefType || await repository.Get(granted.BusinessRefId, cancellationToken) is not { } deadLetter
            || deadLetter.DiscardGrantedBy(granted.CheckerId))
        {
            return;
        }

        if (deadLetter.ApproveDiscard(granted.CheckerId, granted.SubjectHash).IsFailure)
        {
            throw new PrerequisiteMissingException(PrerequisiteCodes.ApprovalNotApplied, $"{granted.ApprovalType}:{granted.ApprovalId}");
        }

        await DeadLetterDiscards.Release(deadLetter, HeldEvent.Of(deadLetter), releases, audit, granted.CheckerId, cancellationToken);
    }

    public async Task Handle(ApprovalRejected rejected, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (rejected.BusinessRefType == DiscardApproval.BusinessRefType && await repository.Get(rejected.BusinessRefId, cancellationToken) is { } deadLetter
            && deadLetter.ClearDiscardRequest(rejected.CheckerId, rejected.SubjectHash).IsFailure)
        {
            throw new PrerequisiteMissingException(PrerequisiteCodes.ApprovalEndingNotApplied, $"{rejected.ApprovalType}:{rejected.ApprovalId}");
        }
    }
}
