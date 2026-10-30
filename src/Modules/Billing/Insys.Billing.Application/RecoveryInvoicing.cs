/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Billing
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
using Insys.Billing.Domain.Billing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
using Insys.Health.Contracts;

namespace Insys.Billing.Application;

/// <summary>
/// Wave 5a §4.7: a recovery to be invoiced becomes one invoice to its debtor, with source domain HEALTH_RECOVERY and the recovery as its
/// reference. A one-instalment schedule carries it, as every invoice is billed from a schedule. Paid, it settles the recovery (InvoiceSettled).
/// </summary>
public sealed class RecoveryInvoicing(IBillingRepository billing, IBillingCodes codes, IBusinessDate businessDate, IClock clock) : IIntegrationEventHandler<ProviderRecoveryInvoiceRequested>
{
    public const string Monthly = "MONTHLY";

    public async Task Handle(ProviderRecoveryInvoiceRequested requested, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (await billing.AccountForPolicy(HealthReferences.RecoverySourceDomain, requested.RecoveryId, cancellationToken) is not null)
        {
            return;
        }

        var account = await billing.OpenAccountOf(requested.PayerPartyId, requested.Currency, cancellationToken);
        if (account is null)
        {
            if (requested.CollectionMethod is not { } method || !await codes.CollectionMethodExists(method, businessDate.Today, cancellationToken))
            {
                throw new PrerequisiteMissingException(PrerequisiteCodes.CollectionMethodNotInForce, requested.CollectionMethod ?? "health.cfg_provider_recovery_settings");
            }

            account = BillingAccount.OpenAccount(Guid.CreateVersion7(), requested.PayerPartyId, requested.Currency, method, null, clock.UtcNow);
            billing.Add(account);
        }

        var frequency = await codes.Frequency(Monthly, businessDate.Today, cancellationToken) ?? throw new PrerequisiteMissingException(PrerequisiteCodes.BillingFrequencyNotInForce, Monthly);
        var schedule = account.CreateSchedule(HealthReferences.RecoverySourceDomain, requested.RecoveryId, requested.RecoveryNumber, frequency, requested.DueOn, requested.DueOn.AddDays(1), requested.Amount);
        if (schedule.IsFailure)
        {
            throw new InvalidOperationException($"Recovery {requested.RecoveryNumber} could not be scheduled: {schedule.Error.Message}");
        }

        var issued = await DueInvoices.Issue(account, businessDate.Today, billing, codes, clock, businessDate, cancellationToken, firstBilling: true);
        if (issued.IsFailure)
        {
            throw new InvalidOperationException($"Recovery {requested.RecoveryNumber} could not be invoiced: {issued.Error.Message}");
        }
    }
}
