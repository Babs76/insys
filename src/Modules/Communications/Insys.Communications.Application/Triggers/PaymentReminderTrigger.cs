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
using Insys.Billing.Contracts;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.Communications.Contracts;

namespace Insys.Communications.Application.Triggers;

/// <summary>Payment reminders (F099, addenda/communications.md §6.1): arrears reach a bucket whose action is REMINDER or FINAL_NOTICE.</summary>
public static class PaymentReminderNotices
{
    public const string Reminder = "REMINDER";
    public const string FinalNotice = "FINAL_NOTICE";

    /// <summary>The notice for the bucket's action, to the account's payer; null for any other action or when the payer is not known.</summary>
    public static NotificationRequest? For(ArrearsDetected arrears)
    {
        var type = arrears.NextAction switch
        {
            Reminder => NotificationTypes.PaymentReminder,
            FinalNotice => NotificationTypes.PaymentFinalNotice,
            _ => null,
        };
        if (type is null || arrears.PayerPartyId is not { } payer)
        {
            return null;
        }

        var merge = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["amount_overdue"] = TriggerFormats.Amount(arrears.AmountOverdue),
            ["currency"] = arrears.Currency,
            ["days_overdue"] = TriggerFormats.Number(arrears.DaysOverdue),
        };
        if (arrears.AccountNumber is { } accountNumber)
        {
            merge["account_number"] = accountNumber;
        }

        return new NotificationRequest(type, payer, TriggerSources.BillingAccount, arrears.AccountId, merge, arrears.ProductCode, arrears.OrgNodeCode);
    }
}

public sealed class PaymentReminderTrigger(NotificationQueue queue) : IIntegrationEventHandler<ArrearsDetected>
{
    public async Task Handle(ArrearsDetected arrears, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (PaymentReminderNotices.For(arrears) is { } request)
        {
            await queue.Queue(request, envelope.EventId, cancellationToken);
        }
    }
}
