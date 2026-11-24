/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Workflow.Tests
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
using Insys.BuildingBlocks.Domain;
using Insys.Workflow.Domain.Cases;

namespace Insys.Workflow.Tests;

/// <summary>
/// DE-008 (Ade, 24 November 2026): a redress whose payout failed is PAYMENT_FAILED with the failed payment's reference, and is paid again only with
/// that reference typed and a second person's approval (PAYOUT_RETRY), as a new, gated payout attempt.
/// </summary>
public sealed class CaseRedressRetryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Maker = Guid.CreateVersion7();

    private static CaseRedress Failed(string caseNumber, out Guid payment)
    {
        var c = ServiceCase.Open(Guid.CreateVersion7(), caseNumber, new CaseTypeRules("COMPLAINT", "COMPLAINT", ["POLICY"], RequiresCategory: false), Now, "PHONE",
            new Complainant(Guid.CreateVersion7(), null, null, null), new CaseRaiser("STAFF", Maker, null, null), null, "Summary", false, null, Now.AddDays(2), "staff", Now);
        var redress = CaseRedress.Request(Guid.CreateVersion7(), c.Value, "COMPENSATION", 750.50m, "ZAR", Guid.CreateVersion7(), null, Maker, Now).Value;
        redress.Approve(Guid.CreateVersion7(), redress.SubjectHash, caseNumber, Now).IsSuccess.ShouldBeTrue();
        payment = Guid.CreateVersion7();
        redress.RecordPaymentFailure(payment, "PAY-R-1", "Account closed", Now).ShouldBeTrue();
        return redress;
    }

    [Fact]
    public void AFailedPayout_IsPaymentFailedWithItsReference_OnceOnly_DE008()
    {
        var redress = Failed("CS1", out var payment);
        (redress.Status, redress.FailedPaymentRef, redress.FailureReason).ShouldBe((RedressStatus.PaymentFailed, "PAY-R-1", "Account closed"));
        redress.RecordPaymentFailure(payment, "PAY-R-1", "Account closed", Now).ShouldBeFalse();
        redress.NewHistory.Select(h => h.EventType).ShouldContain(CaseEventTypes.RedressPaymentFailed);
        redress.MarkPaid(Guid.CreateVersion7(), Now).IsFailure.ShouldBeTrue("only an approved redress is paid");
    }

    [Fact]
    public void ARetry_NeedsTheFailedReferenceTyped_AndAReason_DE008()
    {
        var redress = Failed("CS2", out _);
        var account = Guid.CreateVersion7();
        redress.RequestPayoutRetry("PAY-R-2", "New account", account, Maker, "CS2", Now).Error!.Code.ShouldBe(ErrorCodes.TypedReferenceMismatch);
        redress.RetryRequestedBy.ShouldBeNull();
        redress.DomainEvents.OfType<RedressPayoutRetryRequested>().ShouldBeEmpty();
        redress.RequestPayoutRetry("PAY-R-1", "", account, Maker, "CS2", Now).IsFailure.ShouldBeTrue();

        redress.RequestPayoutRetry("PAY-R-1", "New account", account, Maker, "CS2", Now).IsSuccess.ShouldBeTrue();
        redress.DomainEvents.OfType<RedressPayoutRetryRequested>().Single().Amount.ShouldBe(750.50m);
        redress.RequestPayoutRetry("PAY-R-1", "Again", account, Maker, "CS2", Now).Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
    }

    [Fact]
    public void AGrantedRetry_IsApprovedAgainWithTheNextAttempt_AndARejectedOneStaysPaymentFailed_DE008_SRS002()
    {
        var redress = Failed("CS3", out _);
        var account = Guid.CreateVersion7();
        redress.RequestPayoutRetry("PAY-R-1", "New account", account, Maker, "CS3", Now);
        var rejection = Guid.CreateVersion7();
        redress.DecidePayoutRetry(false, rejection, Guid.CreateVersion7(), redress.RetrySubjectHash, "REJECTED", Now).IsSuccess.ShouldBeTrue();
        (redress.Status, redress.RetryRequestedBy, redress.Attempt).ShouldBe((RedressStatus.PaymentFailed, (Guid?)null, 1));
        redress.RetryDecidedBy(rejection).ShouldBeTrue();

        redress.RequestPayoutRetry("PAY-R-1", "New account", account, Maker, "CS3", Now);
        redress.DecidePayoutRetry(true, Guid.CreateVersion7(), Guid.CreateVersion7(), "another subject", null, Now).IsFailure.ShouldBeTrue();
        redress.DecidePayoutRetry(true, Guid.CreateVersion7(), Guid.CreateVersion7(), redress.RetrySubjectHash, null, Now).IsSuccess.ShouldBeTrue();

        var payout = redress.DomainEvents.OfType<RedressPayoutRetried>().Single();
        (payout.Attempt, payout.Amount, payout.Currency, payout.BankAccountId, payout.PayeePartyId).ShouldBe((2, 750.50m, "ZAR", account, redress.PayeePartyId));
        (redress.Status, redress.PayoutBankAccountId).ShouldBe((RedressStatus.Approved, (Guid?)account));
        redress.MarkPaid(Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
    }
}
