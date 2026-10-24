/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Communications
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
using Insys.Communications.Contracts;
using Insys.General.Contracts;
using Insys.Life.Contracts;
using Microsoft.Extensions.Logging;

namespace Insys.Communications.Application.Triggers;

/// <summary>The claim statuses that notify (OQ-086, addenda/communications.md §6.3).</summary>
public static class ClaimNoticeStatuses
{
    public const string Registered = "REGISTERED";
    public const string Approved = "APPROVED";
    public const string Declined = "DECLINED";
    public const string Settled = "SETTLED";
    public const string Closed = "CLOSED";
}

/// <summary>
/// CLAIM_STATUS for General and Life claims (F101, §6.3): the claim number, its new status and the date. The amount
/// appears only on settlement (OQ-086); a decline never carries its reason.
/// </summary>
public static class ClaimStatusNotices
{
    public static NotificationRequest For(GeneralClaimRegistered e, NoticeRecipient to, DateOnly on) =>
        Request(TriggerSources.GeneralClaim, e.ClaimId, e.ClaimNumber, ClaimNoticeStatuses.Registered, to, on);

    public static NotificationRequest For(GeneralClaimApproved e, NoticeRecipient to, DateOnly on) =>
        Request(TriggerSources.GeneralClaim, e.ClaimId, e.ClaimNumber, ClaimNoticeStatuses.Approved, to, on);

    public static NotificationRequest For(GeneralClaimDeclined e, NoticeRecipient to, DateOnly on) =>
        Request(TriggerSources.GeneralClaim, e.ClaimId, e.ClaimNumber, ClaimNoticeStatuses.Declined, to, on);

    public static NotificationRequest For(GeneralClaimSettled e, NoticeRecipient to, DateOnly on) =>
        Request(TriggerSources.GeneralClaim, e.ClaimId, e.ClaimNumber, ClaimNoticeStatuses.Settled, to, on, e.Amount, e.Currency);

    public static NotificationRequest For(GeneralClaimClosed e, NoticeRecipient to, DateOnly on) =>
        Request(TriggerSources.GeneralClaim, e.ClaimId, e.ClaimNumber, ClaimNoticeStatuses.Closed, to, on);

    public static NotificationRequest For(LifeClaimRegistered e, NoticeRecipient to, DateOnly on) =>
        Request(TriggerSources.LifeClaim, e.ClaimId, e.ClaimNumber, ClaimNoticeStatuses.Registered, to, on);

    public static NotificationRequest For(LifeClaimApproved e, NoticeRecipient to, DateOnly on) =>
        Request(TriggerSources.LifeClaim, e.ClaimId, e.ClaimNumber, ClaimNoticeStatuses.Approved, to, on);

    public static NotificationRequest For(LifeClaimDeclined e, NoticeRecipient to, DateOnly on) =>
        Request(TriggerSources.LifeClaim, e.ClaimId, e.ClaimNumber, ClaimNoticeStatuses.Declined, to, on);

    public static NotificationRequest For(LifeClaimSettled e, NoticeRecipient to, DateOnly on) =>
        Request(TriggerSources.LifeClaim, e.ClaimId, e.ClaimNumber, ClaimNoticeStatuses.Settled, to, on, e.Amount, e.Currency);

    private static NotificationRequest Request(string sourceType, Guid claimId, string claimNumber, string status, NoticeRecipient to, DateOnly on,
        decimal? amount = null, string? currency = null)
    {
        var merge = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["claim_number"] = claimNumber,
            ["status"] = status,
            ["status_date"] = TriggerFormats.Date(on),
        };
        if (amount is { } paid && currency is not null)
        {
            merge["amount"] = TriggerFormats.Amount(paid);
            merge["currency"] = currency;
        }

        return new NotificationRequest(NotificationTypes.ClaimStatus, to.PartyId, sourceType, claimId, merge, to.ProductCode, to.OrgNodeCode);
    }
}

/// <summary>
/// Resolves a notice's recipient through the owning module's public query and queues the notice. A record with no
/// recipient sends nothing and is logged by its ID only. A missing configuration parks the delivery (ADR-022): the
/// exception from the queue is never caught here.
/// </summary>
internal static class TriggerQueueing
{
    public static async Task Notify(NotificationQueue queue, ILogger logger, string sourceType, Guid sourceId, NoticeRecipient? recipient,
        Func<NoticeRecipient, NotificationRequest> map, Guid eventId, CancellationToken cancellationToken)
    {
        if (recipient is null)
        {
            logger.LogInformation("No notice recipient for {SourceType} {SourceId}; nothing queued", sourceType, sourceId);
            return;
        }

        await queue.Queue(map(recipient), eventId, cancellationToken);
    }
}

public sealed class GeneralClaimStatusTrigger(IGeneralClaimRecipients recipients, NotificationQueue queue, IBusinessDate businessDate,
    ILogger<GeneralClaimStatusTrigger> logger) :
    IIntegrationEventHandler<GeneralClaimRegistered>,
    IIntegrationEventHandler<GeneralClaimApproved>,
    IIntegrationEventHandler<GeneralClaimDeclined>,
    IIntegrationEventHandler<GeneralClaimSettled>,
    IIntegrationEventHandler<GeneralClaimClosed>
{
    public Task Handle(GeneralClaimRegistered e, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Notify(e.ClaimId, envelope, to => ClaimStatusNotices.For(e, to, businessDate.DateOf(envelope.OccurredAt)), cancellationToken);

    public Task Handle(GeneralClaimApproved e, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Notify(e.ClaimId, envelope, to => ClaimStatusNotices.For(e, to, businessDate.DateOf(envelope.OccurredAt)), cancellationToken);

    public Task Handle(GeneralClaimDeclined e, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Notify(e.ClaimId, envelope, to => ClaimStatusNotices.For(e, to, businessDate.DateOf(envelope.OccurredAt)), cancellationToken);

    public Task Handle(GeneralClaimSettled e, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Notify(e.ClaimId, envelope, to => ClaimStatusNotices.For(e, to, businessDate.DateOf(envelope.OccurredAt)), cancellationToken);

    public Task Handle(GeneralClaimClosed e, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Notify(e.ClaimId, envelope, to => ClaimStatusNotices.For(e, to, businessDate.DateOf(envelope.OccurredAt)), cancellationToken);

    private async Task Notify(Guid claimId, EventEnvelope envelope, Func<NoticeRecipient, NotificationRequest> map, CancellationToken cancellationToken) =>
        await TriggerQueueing.Notify(queue, logger, TriggerSources.GeneralClaim, claimId, await recipients.ForClaim(envelope.TenantId, claimId, cancellationToken),
            map, envelope.EventId, cancellationToken);
}

public sealed class LifeClaimStatusTrigger(ILifeClaimRecipients recipients, NotificationQueue queue, IBusinessDate businessDate,
    ILogger<LifeClaimStatusTrigger> logger) :
    IIntegrationEventHandler<LifeClaimRegistered>,
    IIntegrationEventHandler<LifeClaimApproved>,
    IIntegrationEventHandler<LifeClaimDeclined>,
    IIntegrationEventHandler<LifeClaimSettled>
{
    public Task Handle(LifeClaimRegistered e, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Notify(e.ClaimId, envelope, to => ClaimStatusNotices.For(e, to, businessDate.DateOf(envelope.OccurredAt)), cancellationToken);

    public Task Handle(LifeClaimApproved e, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Notify(e.ClaimId, envelope, to => ClaimStatusNotices.For(e, to, businessDate.DateOf(envelope.OccurredAt)), cancellationToken);

    public Task Handle(LifeClaimDeclined e, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Notify(e.ClaimId, envelope, to => ClaimStatusNotices.For(e, to, businessDate.DateOf(envelope.OccurredAt)), cancellationToken);

    public Task Handle(LifeClaimSettled e, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Notify(e.ClaimId, envelope, to => ClaimStatusNotices.For(e, to, businessDate.DateOf(envelope.OccurredAt)), cancellationToken);

    private async Task Notify(Guid claimId, EventEnvelope envelope, Func<NoticeRecipient, NotificationRequest> map, CancellationToken cancellationToken) =>
        await TriggerQueueing.Notify(queue, logger, TriggerSources.LifeClaim, claimId, await recipients.ForClaim(envelope.TenantId, claimId, cancellationToken),
            map, envelope.EventId, cancellationToken);
}
