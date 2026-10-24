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
using Insys.Health.Contracts;
using Microsoft.Extensions.Logging;

namespace Insys.Communications.Application.Triggers;

/// <summary>The statuses of a Health notice (F031, addenda/communications.md §6.3).</summary>
public static class HealthNoticeStatuses
{
    public const string Adjudicated = "ADJUDICATED";
    public const string Pended = "PENDED";
    public const string Paid = "PAID";
    public const string Rejected = "REJECTED";
    public const string Reversed = "REVERSED";
    public const string Approved = "APPROVED";
}

/// <summary>
/// The member's Health notices (F031, §6.3): the claim or authorisation number, status and date only. Never a reason,
/// a diagnosis, a procedure or a line; an amount only as the total paid (HLT-008, POPIA s26).
/// </summary>
public static class HealthDecisionNotices
{
    public static NotificationRequest For(MedicalClaimAdjudicated e, NoticeRecipient to, DateOnly on) =>
        Claim(e.ClaimId, e.ClaimNumber, e.Pended ? HealthNoticeStatuses.Pended : HealthNoticeStatuses.Adjudicated, to, on);

    public static NotificationRequest For(MedicalClaimSettled e, NoticeRecipient to, DateOnly on) =>
        Claim(e.ClaimId, e.ClaimNumber, HealthNoticeStatuses.Paid, to, on, e.Amount, e.Currency);

    public static NotificationRequest For(MedicalClaimRejected e, NoticeRecipient to, DateOnly on) =>
        Claim(e.ClaimId, e.ClaimNumber, HealthNoticeStatuses.Rejected, to, on);

    public static NotificationRequest For(MedicalClaimReversed e, NoticeRecipient to, DateOnly on) =>
        Claim(e.ClaimId, e.ClaimNumber, HealthNoticeStatuses.Reversed, to, on);

    public static NotificationRequest For(PreauthorisationApproved e, NoticeRecipient to) =>
        Preauth(e.PreauthorisationId, e.AuthNumber, HealthNoticeStatuses.Approved, to, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["valid_from"] = TriggerFormats.Date(e.ValidFrom),
            ["valid_to"] = TriggerFormats.Date(e.ValidTo),
        });

    public static NotificationRequest For(PreauthorisationRejected e, NoticeRecipient to) =>
        Preauth(e.PreauthorisationId, e.AuthNumber, HealthNoticeStatuses.Rejected, to, new Dictionary<string, string>(StringComparer.Ordinal));

    private static NotificationRequest Claim(Guid claimId, string claimNumber, string status, NoticeRecipient to, DateOnly on, decimal? totalPaid = null,
        string? currency = null)
    {
        var merge = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["claim_number"] = claimNumber,
            ["status"] = status,
            ["status_date"] = TriggerFormats.Date(on),
        };
        if (totalPaid is { } paid && currency is not null)
        {
            merge["total_paid"] = TriggerFormats.Amount(paid);
            merge["currency"] = currency;
        }

        return new NotificationRequest(NotificationTypes.HealthClaimDecision, to.PartyId, TriggerSources.HealthClaim, claimId, merge, to.ProductCode, to.OrgNodeCode);
    }

    private static NotificationRequest Preauth(Guid preauthorisationId, string authNumber, string status, NoticeRecipient to, Dictionary<string, string> merge)
    {
        merge["auth_number"] = authNumber;
        merge["status"] = status;
        return new NotificationRequest(NotificationTypes.PreauthDecision, to.PartyId, TriggerSources.HealthPreauth, preauthorisationId, merge, to.ProductCode,
            to.OrgNodeCode);
    }
}

public sealed class HealthClaimDecisionTrigger(IHealthNoticeRecipients recipients, NotificationQueue queue, IBusinessDate businessDate,
    ILogger<HealthClaimDecisionTrigger> logger) :
    IIntegrationEventHandler<MedicalClaimAdjudicated>,
    IIntegrationEventHandler<MedicalClaimSettled>,
    IIntegrationEventHandler<MedicalClaimRejected>,
    IIntegrationEventHandler<MedicalClaimReversed>
{
    public Task Handle(MedicalClaimAdjudicated e, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Notify(e.ClaimId, envelope, to => HealthDecisionNotices.For(e, to, businessDate.DateOf(envelope.OccurredAt)), cancellationToken);

    public Task Handle(MedicalClaimSettled e, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Notify(e.ClaimId, envelope, to => HealthDecisionNotices.For(e, to, businessDate.DateOf(envelope.OccurredAt)), cancellationToken);

    public Task Handle(MedicalClaimRejected e, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Notify(e.ClaimId, envelope, to => HealthDecisionNotices.For(e, to, businessDate.DateOf(envelope.OccurredAt)), cancellationToken);

    public Task Handle(MedicalClaimReversed e, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Notify(e.ClaimId, envelope, to => HealthDecisionNotices.For(e, to, businessDate.DateOf(envelope.OccurredAt)), cancellationToken);

    private async Task Notify(Guid claimId, EventEnvelope envelope, Func<NoticeRecipient, NotificationRequest> map, CancellationToken cancellationToken) =>
        await TriggerQueueing.Notify(queue, logger, TriggerSources.HealthClaim, claimId, await recipients.ForClaim(envelope.TenantId, claimId, cancellationToken),
            map, envelope.EventId, cancellationToken);
}

public sealed class PreauthDecisionTrigger(IHealthNoticeRecipients recipients, NotificationQueue queue, ILogger<PreauthDecisionTrigger> logger) :
    IIntegrationEventHandler<PreauthorisationApproved>,
    IIntegrationEventHandler<PreauthorisationRejected>
{
    public Task Handle(PreauthorisationApproved e, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Notify(e.PreauthorisationId, envelope, to => HealthDecisionNotices.For(e, to), cancellationToken);

    public Task Handle(PreauthorisationRejected e, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Notify(e.PreauthorisationId, envelope, to => HealthDecisionNotices.For(e, to), cancellationToken);

    private async Task Notify(Guid preauthorisationId, EventEnvelope envelope, Func<NoticeRecipient, NotificationRequest> map, CancellationToken cancellationToken) =>
        await TriggerQueueing.Notify(queue, logger, TriggerSources.HealthPreauth, preauthorisationId,
            await recipients.ForPreauthorisation(envelope.TenantId, preauthorisationId, cancellationToken), map, envelope.EventId, cancellationToken);
}
