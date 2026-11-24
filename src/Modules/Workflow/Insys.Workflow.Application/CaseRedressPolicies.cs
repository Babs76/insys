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
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
using Insys.Party.Contracts;
using Insys.Payments.Contracts;
using Insys.Workflow.Contracts;
using Insys.Workflow.Domain.Cases;

namespace Insys.Workflow.Application;

/// <summary>
/// Redress on a case is Workflow's own approval (OQ-104): granted, it is APPROVED and Payments is told to pay it
/// (CaseRedressApproved); ended any other way (rejected, expired, withdrawn) it is REJECTED; paid (payment.PaymentSucceeded
/// for business reference CASE_REDRESS), it is PAID with the payment. DE-008: its payout failing (payment.PaymentFailed) makes it PAYMENT_FAILED, and a
/// retry a second person approves (PAYOUT_RETRY) makes it APPROVED again with a new payout. A redress that will not take the decision parks the
/// delivery (APPROVAL_NOT_APPLIED, APPROVAL_ENDING_NOT_APPLIED; ADR-022, D-5); a repeated delivery changes nothing.
/// </summary>
public sealed class CaseRedressPolicies(ICaseRepository cases, IClock clock) :
    IIntegrationEventHandler<Contracts.ApprovalGranted>,
    IIntegrationEventHandler<Contracts.ApprovalRejected>,
    IIntegrationEventHandler<PaymentSucceeded>,
    IIntegrationEventHandler<PaymentFailed>
{
    public async Task Handle(Contracts.ApprovalGranted granted, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (granted.BusinessRefType != CaseApprovals.RedressBusinessRefType || await cases.Redress(granted.BusinessRefId, cancellationToken) is not { } redress)
        {
            return;
        }

        if (granted.ApprovalType == PaymentApprovals.PayoutRetryApprovalType)
        {
            if (!redress.RetryDecidedBy(granted.ApprovalId) && redress.DecidePayoutRetry(true, granted.ApprovalId, granted.CheckerId, granted.SubjectHash, null, clock.UtcNow).IsFailure)
            {
                throw new PrerequisiteMissingException(PrerequisiteCodes.ApprovalNotApplied, $"{granted.ApprovalType}:{granted.ApprovalId}");
            }

            return;
        }

        if (redress.Status is RedressStatus.Approved or RedressStatus.Paid or RedressStatus.PaymentFailed && redress.DecidedBy == granted.CheckerId)
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
        if (rejected.BusinessRefType != CaseApprovals.RedressBusinessRefType || await cases.Redress(rejected.BusinessRefId, cancellationToken) is not { } redress)
        {
            return;
        }

        var checker = rejected.CheckerId == Guid.Empty ? (Guid?)null : rejected.CheckerId;
        if (rejected.ApprovalType == PaymentApprovals.PayoutRetryApprovalType)
        {
            if (!redress.RetryDecidedBy(rejected.ApprovalId) && redress.DecidePayoutRetry(false, rejected.ApprovalId, checker, rejected.SubjectHash, rejected.Ending, clock.UtcNow).IsFailure)
            {
                throw new PrerequisiteMissingException(PrerequisiteCodes.ApprovalEndingNotApplied, $"{rejected.ApprovalType}:{rejected.ApprovalId}");
            }

            return;
        }

        if (redress.Status == RedressStatus.Rejected)
        {
            return;
        }

        if (redress.Reject(checker, rejected.Ending, clock.UtcNow).IsFailure)
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

    /// <summary>DE-008: the redress's payout failed. PAYMENT_FAILED with the failed payment's reference; a redelivery changes nothing.</summary>
    public async Task Handle(PaymentFailed failed, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (failed.BusinessRefType == CaseApprovals.RedressBusinessRefType && failed.BusinessRefId is { } id && await cases.Redress(id, cancellationToken) is { } redress)
        {
            redress.RecordPaymentFailure(failed.PaymentId, failed.PaymentRef, failed.Reason, clock.UtcNow);
        }
    }
}

/// <summary>
/// DE-008: a redress whose payout failed is asked to be paid again, for a second person's approval (PAYOUT_RETRY). The account is the one named (the
/// payee's own, verified and ACTIVE, in the redress's currency, ADR-013), else the one its last payout went to, else the payee's current verified account
/// in the currency, as Payments chose for the first payout. No cancelling a failed redress: that is not decided.
/// </summary>
internal sealed class RetryRedressPayoutHandler(ICaseRepository cases, CaseAccess access, IBankAccounts bankAccounts, ITenantContext tenant, IClock clock, IAuditTrail audit)
    : ICommandHandler<RetryRedressPayout, long>
{
    public async Task<Result<long>> Handle(RetryRedressPayout command, CancellationToken cancellationToken)
    {
        var loaded = await access.Load(cases, command.CaseId, null, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        if (await cases.Redress(command.RedressId, cancellationToken) is not { } redress || redress.CaseId != command.CaseId)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The redress was not found.");
        }

        if (redress.RowVersion != command.ExpectedVersion)
        {
            return Error.PreconditionFailed(ErrorCodes.PreconditionFailed, "The redress has changed since it was read. Read it again and retry.");
        }

        if (access.UserId == Guid.Empty)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Redress is paid again by identified users.");
        }

        var tenantId = tenant.RequireTenantId();
        Guid accountId;
        if (command.BankAccountId is { } named)
        {
            if (await bankAccounts.Get(tenantId, named, cancellationToken) is not { IsPayable: true } account || account.PartyId != redress.PayeePartyId
                || !string.Equals(account.Currency, redress.Currency, StringComparison.Ordinal))
            {
                return Error.Validation(ErrorCodes.ValidationFailed, $"The account named is not a verified, active account of the payee in {redress.Currency}.");
            }

            accountId = account.Id;
        }
        else if ((redress.PayoutBankAccountId ?? redress.BankAccountId) is { } previous)
        {
            accountId = previous;
        }
        else if (await bankAccounts.Current(tenantId, redress.PayeePartyId, redress.Currency, cancellationToken) is { IsPayable: true } current)
        {
            accountId = current.Id;
        }
        else
        {
            return Error.Validation(ErrorCodes.ValidationFailed, $"The payee has no verified, active account in {redress.Currency}; name one.");
        }

        audit.Record(new AuditEntry("CASE", "RetryRedressPayout", nameof(CaseRedress), redress.Id.ToString(),
            new Dictionary<string, string?> { ["case"] = command.CaseId.ToString(), ["bankAccountChanged"] = (command.BankAccountId is not null).ToString() }));
        var requested = redress.RequestPayoutRetry(command.PaymentRef, command.Reason, accountId, access.UserId, loaded.Value.CaseNumber, clock.UtcNow);
        return requested.IsFailure ? requested.Error : redress.RowVersion;
    }
}
