/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Workflow
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
using Insys.Payments.Contracts;
using Insys.Workflow.Contracts;
using Insys.Workflow.Domain.Cases;

namespace Insys.Workflow.Application;

/// <summary>
/// Redress on a case is Workflow's own approval (OQ-104): granted, it is APPROVED and Payments is told to pay it
/// (CaseRedressApproved); ended any other way (rejected, expired, withdrawn) it is REJECTED; paid (payment.PaymentSucceeded
/// for business reference CASE_REDRESS), it is PAID with the payment. A redress that will not take the decision parks the
/// delivery (APPROVAL_NOT_APPLIED, APPROVAL_ENDING_NOT_APPLIED; ADR-022, D-5); a repeated delivery changes nothing.
/// </summary>
public sealed class CaseRedressPolicies(ICaseRepository cases, IClock clock) :
    IIntegrationEventHandler<Contracts.ApprovalGranted>,
    IIntegrationEventHandler<Contracts.ApprovalRejected>,
    IIntegrationEventHandler<PaymentSucceeded>
{
    public async Task Handle(Contracts.ApprovalGranted granted, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (granted.BusinessRefType != CaseApprovals.RedressBusinessRefType || await cases.Redress(granted.BusinessRefId, cancellationToken) is not { } redress
            || (redress.Status is RedressStatus.Approved or RedressStatus.Paid && redress.DecidedBy == granted.CheckerId))
        {
            return;
        }

        var caseNumber = (await cases.Get(redress.CaseId, cancellationToken))?.CaseNumber ?? "";
        if (redress.Approve(granted.CheckerId, granted.SubjectHash, caseNumber, clock.UtcNow).IsFailure)
        {
            throw new PrerequisiteMissingException(PrerequisiteCodes.ApprovalNotApplied, $"{granted.ApprovalType}:{granted.ApprovalId}");
        }
    }

    public async Task Handle(Contracts.ApprovalRejected rejected, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (rejected.BusinessRefType != CaseApprovals.RedressBusinessRefType || await cases.Redress(rejected.BusinessRefId, cancellationToken) is not { } redress
            || redress.Status == RedressStatus.Rejected)
        {
            return;
        }

        if (redress.Reject(rejected.CheckerId == Guid.Empty ? null : rejected.CheckerId, rejected.Ending, clock.UtcNow).IsFailure)
        {
            throw new PrerequisiteMissingException(PrerequisiteCodes.ApprovalEndingNotApplied, $"{rejected.ApprovalType}:{rejected.ApprovalId}");
        }
    }

    public async Task Handle(PaymentSucceeded succeeded, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (succeeded.BusinessRefType == CaseApprovals.RedressBusinessRefType && succeeded.BusinessRefId is { } id
            && await cases.Redress(id, cancellationToken) is { Status: RedressStatus.Approved } redress)
        {
            redress.MarkPaid(succeeded.PaymentId, clock.UtcNow);
        }
    }
}
