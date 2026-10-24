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
using System.Globalization;
using Insys.Billing.Contracts;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;

namespace Insys.Billing.Application;

/// <summary>
/// Request 46. Opens an account's arrears case when one of its invoices passes its due date, so the first daily run that
/// finds a premium overdue opens the case: the run itself opens it when a bucket covers the days overdue, and this finds
/// it open. And ages on a case that went past every bucket. Either way, with no bucket in force for the days overdue the
/// delivery is parked (ADR-022), which alerts, and it is retried when buckets are published.
/// </summary>
public sealed class ArrearsOnOverdue(IBillingRepository billing, IBillingCodes codes, IBusinessDate businessDate, IClock clock) :
    IIntegrationEventHandler<InvoiceOverdue>,
    IIntegrationEventHandler<ArrearsAgeingBlocked>
{
    public async Task Handle(ArrearsAgeingBlocked blocked, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (await billing.Account(blocked.AccountId, cancellationToken) is not { } account || !Ageing.HasOpenCase(account))
        {
            return;
        }

        var today = businessDate.Today;
        var invoices = await billing.Invoices(account.Id, cancellationToken);
        var days = Ageing.DaysOverdue(invoices.Where(i => i.Outstanding > 0 && i.DueDate < today).ToList(), today);
        if (days > 0 && !(await codes.AgeingBuckets(today, cancellationToken)).Any(b => days >= b.DaysFrom && days <= b.DaysTo))
        {
            throw new PrerequisiteMissingException(PrerequisiteCodes.ArrearsAgeingBucketNotInForce, days.ToString(CultureInfo.InvariantCulture));
        }

        var result = await Ageing.Detect(account, invoices, today, codes, clock.UtcNow, cancellationToken);
        if (result.IsFailure)
        {
            throw new InvalidOperationException($"Account {account.AccountNumber} cannot be aged: {result.Error.Code}");
        }
    }

    public async Task Handle(InvoiceOverdue overdue, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (await billing.Account(overdue.BillingAccountId, cancellationToken) is not { } account || Ageing.HasOpenCase(account))
        {
            return;
        }

        var today = businessDate.Today;
        var invoices = await billing.Invoices(account.Id, cancellationToken);
        var result = await Ageing.Detect(account, invoices, today, codes, clock.UtcNow, cancellationToken);
        if (result is { IsFailure: true, Error.Code: Domain.Billing.BillingErrorCodes.NoAgeingBucket })
        {
            var days = Ageing.DaysOverdue(invoices.Where(i => i.Outstanding > 0 && i.DueDate < today).ToList(), today);
            throw new PrerequisiteMissingException(PrerequisiteCodes.ArrearsAgeingBucketNotInForce, days.ToString(CultureInfo.InvariantCulture));
        }

        if (result.IsFailure)
        {
            throw new InvalidOperationException($"Account {account.AccountNumber} cannot be aged: {result.Error.Code}");
        }
    }
}
