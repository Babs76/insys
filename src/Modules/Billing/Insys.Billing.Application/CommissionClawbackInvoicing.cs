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
using Insys.Distribution.Contracts;

namespace Insys.Billing.Application;

/// <summary>
/// Ade (24 November 2026, OQ-062): what a carried-forward commission statement still carries, once a second person grants its
/// invoice, becomes one invoice to the intermediary's party, with source domain DIST_CLAW and the statement as its reference,
/// as Health's recoveries are invoiced (RecoveryInvoicing). A one-instalment schedule carries it, as every invoice is billed from a
/// schedule. The party's open account in the currency is used. TODO(spec): a party with no open account needs a collection method,
/// which no setting names for intermediaries (OQ-062): the delivery parks until the party has an account, never a guessed method.
/// </summary>
public sealed class CommissionClawbackInvoicing(IBillingRepository billing, IBillingCodes codes, IBusinessDate businessDate, IClock clock)
    : IIntegrationEventHandler<CommissionClawbackInvoiceRequested>
{
    public async Task Handle(CommissionClawbackInvoiceRequested requested, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (await billing.AccountForPolicy(DistributionReferences.ClawbackSourceDomain, requested.StatementId, cancellationToken) is not null)
        {
            return;
        }

        var account = await billing.OpenAccountOf(requested.PayerPartyId, requested.Currency, cancellationToken)
                      ?? throw new PrerequisiteMissingException(PrerequisiteCodes.CollectionMethodNotInForce,
                          $"{DistributionReferences.ClawbackSourceDomain}:{requested.Reference}:{requested.Currency}");

        var frequency = await codes.Frequency(RecoveryInvoicing.Monthly, businessDate.Today, cancellationToken)
                        ?? throw new PrerequisiteMissingException(PrerequisiteCodes.BillingFrequencyNotInForce, RecoveryInvoicing.Monthly);
        var schedule = account.CreateSchedule(DistributionReferences.ClawbackSourceDomain, requested.StatementId, requested.Reference, frequency, requested.DueOn,
            requested.DueOn.AddDays(1), requested.Amount);
        if (schedule.IsFailure)
        {
            throw new InvalidOperationException($"The clawback of commission statement {requested.Reference} could not be scheduled: {schedule.Error.Message}");
        }

        var issued = await DueInvoices.Issue(account, businessDate.Today, billing, codes, clock, businessDate, cancellationToken, firstBilling: true);
        if (issued.IsFailure)
        {
            throw new InvalidOperationException($"The clawback of commission statement {requested.Reference} could not be invoiced: {issued.Error.Message}");
        }
    }
}
