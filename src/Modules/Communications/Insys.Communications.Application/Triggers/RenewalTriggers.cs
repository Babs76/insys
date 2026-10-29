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
using System.Security.Cryptography;
using System.Text;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.Communications.Contracts;
using Insys.General.Contracts;

namespace Insys.Communications.Application.Triggers;

/// <summary>Renewal invitations and reminders for General policies (F021, F100, addenda/communications.md §6.2, OQ-089).</summary>
public static class RenewalNotices
{
    /// <summary>The invitation, to the policyholder, with a reviewed renewal's offered premium (Wave 4 §4.2); null when the event does not name one.</summary>
    public static NotificationRequest? For(GeneralRenewalInvited invited)
    {
        if (invited.PolicyholderPartyId is not { } policyholder)
        {
            return null;
        }

        var merge = Merge(invited.PolicyNumber, invited.ProductCode, invited.PeriodEnd);
        if (invited.OfferedPremium is { } offered && invited.Currency is { } currency)
        {
            merge["renewal_premium"] = TriggerFormats.Amount(offered);
            merge["currency"] = currency;
        }

        return new NotificationRequest(NotificationTypes.RenewalInvitation, policyholder, TriggerSources.GeneralPolicy, invited.PolicyId, merge, invited.ProductCode);
    }

    /// <summary>The reminder, to the policyholder, with the days left before the period ends.</summary>
    public static NotificationRequest For(GeneralRenewalReminderDue due)
    {
        var merge = Merge(due.PolicyNumber, due.ProductCode, due.PeriodEnd);
        merge["days_to_renewal"] = TriggerFormats.Number(due.DaysBefore);
        return new NotificationRequest(NotificationTypes.RenewalReminder, due.PolicyholderPartyId, TriggerSources.GeneralPolicy, due.PolicyId, merge,
            due.ProductCode, due.OrgNodeCode);
    }

    /// <summary>
    /// The same key for a policy, period end and reminder day every time, used in place of the event ID: the daily job
    /// may raise the same reminder twice, and the second queues nothing.
    /// </summary>
    public static Guid ReminderKey(Guid policyId, DateOnly periodEnd, int daysBefore)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{NotificationTypes.RenewalReminder}:{policyId:N}|{TriggerFormats.Date(periodEnd)}|{TriggerFormats.Number(daysBefore)}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static Dictionary<string, string> Merge(string policyNumber, string productCode, DateOnly periodEnd) => new(StringComparer.Ordinal)
    {
        ["policy_number"] = policyNumber,
        ["product_code"] = productCode,
        ["period_end"] = TriggerFormats.Date(periodEnd),
    };
}

public sealed class RenewalInvitationTrigger(NotificationQueue queue) : IIntegrationEventHandler<GeneralRenewalInvited>
{
    public async Task Handle(GeneralRenewalInvited invited, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (RenewalNotices.For(invited) is { } request)
        {
            await queue.Queue(request, envelope.EventId, cancellationToken);
        }
    }
}

public sealed class RenewalReminderTrigger(NotificationQueue queue) : IIntegrationEventHandler<GeneralRenewalReminderDue>
{
    public Task Handle(GeneralRenewalReminderDue due, EventEnvelope envelope, CancellationToken cancellationToken) =>
        queue.Queue(RenewalNotices.For(due), RenewalNotices.ReminderKey(due.PolicyId, due.PeriodEnd, due.DaysBefore), cancellationToken);
}
