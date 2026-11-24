/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Billing.Tests
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
using Insys.BuildingBlocks.Domain;

namespace Insys.Billing.Tests;

/// <summary>
/// The money group (Ade, 24 November 2026): a refund's failed payout is paid again only on a second person's approval, or the refund is
/// cancelled (DE-008); a credit note says what its invoice still asks for (DE-009); a credit note whose reason requires approval waits for
/// one and then credits at most what is still outstanding (DE-010, OQ-058).
/// </summary>
public sealed class MoneyGroupTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Start = new(2026, 12, 1);
    private const string FailedRef = "PAY-0000FAILED1";

    private static (BillingAccount Account, Invoice Invoice) Issued(decimal instalment = 300m)
    {
        var account = BillingAccount.OpenAccount(Guid.CreateVersion7(), Guid.CreateVersion7(), "ZAR", "DEBIT_ORDER", null, Now);
        var schedule = account.CreateSchedule("GENERAL", Guid.CreateVersion7(), "MOTOR-v1", new BillingFrequency("MONTHLY", 12), Start, null, instalment).Value;
        var invoice = Invoice.IssueInvoice(Guid.CreateVersion7(), "INV-", account, schedule, account.DueInstalments(Start).Select(d => d.Instalment).ToList(), "DEBIT_ORDER",
            Start, Now).Value;
        return (account, invoice);
    }

    private static (RefundRequest Refund, Guid Payment, Guid Maker) FailedRefund()
    {
        var (account, _) = Issued();
        var maker = Guid.CreateVersion7();
        var refund = RefundRequest.RequestRefund(Guid.CreateVersion7(), account, 50m, "OVERPAID", Guid.CreateVersion7(), new string('a', 64), maker, Now).Value;
        refund.Decide(true, Guid.CreateVersion7(), refund.SubjectHash, Now, Guid.CreateVersion7()).IsSuccess.ShouldBeTrue();
        var payment = Guid.CreateVersion7();
        refund.LinkPayment(payment);
        refund.MarkPaymentFailed(payment, FailedRef, "Account closed").ShouldBeTrue();
        return (refund, payment, maker);
    }

    [Fact]
    public void ARefundsFailedPayout_MakesItPaymentFailed_WithTheFailedPaymentsReference_AndARedeliveryChangesNothing_DE008()
    {
        var (refund, payment, _) = FailedRefund();

        (refund.Status, refund.FailedPaymentId, refund.FailedPaymentRef, refund.FailureReason).ShouldBe((RefundStatus.PaymentFailed, payment, FailedRef, "Account closed"));
        refund.MarkPaymentFailed(payment, FailedRef, "Account closed").ShouldBeFalse();
        refund.MarkPaid(payment).IsFailure.ShouldBeTrue("a failed payout is never treated as paid");
    }

    [Fact]
    public void ARefundPayoutRetry_NeedsTheFailedPaymentsReferenceTyped_AndChangesNothingOtherwise_DE008()
    {
        var (refund, _, _) = FailedRefund();
        var before = refund.DomainEvents.Count;

        refund.RequestPayoutRetry("PAY-SOMETHINGELSE", "Payer gave a new account", Guid.CreateVersion7(), new string('b', 64), Guid.CreateVersion7(), Now).Error!.Code
            .ShouldBe(ErrorCodes.TypedReferenceMismatch);
        (refund.Status, refund.RetryPending).ShouldBe((RefundStatus.PaymentFailed, false));
        refund.DomainEvents.Count.ShouldBe(before);
    }

    [Fact]
    public void ARefundPayoutRetry_IsOnlyForAFailedPayout_AndOneAtATime_DE008()
    {
        var (account, _) = Issued();
        var approved = RefundRequest.RequestRefund(Guid.CreateVersion7(), account, 50m, "OVERPAID", Guid.CreateVersion7(), new string('a', 64), Guid.CreateVersion7(), Now).Value;
        approved.RequestPayoutRetry(FailedRef, "retry", Guid.CreateVersion7(), "h", Guid.CreateVersion7(), Now).Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition);

        var (refund, _, _) = FailedRefund();
        refund.RequestPayoutRetry(FailedRef, "retry", Guid.CreateVersion7(), "h", Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        refund.RequestPayoutRetry(FailedRef, "again", Guid.CreateVersion7(), "h", Guid.CreateVersion7(), Now).Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
    }

    [Fact]
    public void AGrantedPayoutRetry_ApprovesTheRefundAgainOnItsNextAttempt_ToTheAccountAsked_AndRequestsItsPayout_DE008_SRS002()
    {
        var (refund, failed, _) = FailedRefund();
        var requester = Guid.CreateVersion7();
        var newAccount = Guid.CreateVersion7();
        refund.RequestPayoutRetry($" {FailedRef} ", "Payer gave a new account", newAccount, new string('b', 64), requester, Now).IsSuccess.ShouldBeTrue();
        var asked = refund.DomainEvents.OfType<RefundPayoutRetryRequested>().Single();
        (asked.Attempt, asked.Amount, asked.FailedPaymentRef, asked.SubjectHash).ShouldBe((2, 50m, FailedRef, refund.RetrySubjectHash));
        var approval = Guid.CreateVersion7();

        refund.DecideRetry(true, requester, refund.RetrySubjectHash, approval, Now).Error!.Code.ShouldBe(ErrorCodes.MakerCheckerSamePerson);
        refund.DecideRetry(true, Guid.CreateVersion7(), "another subject", approval, Now).Error!.Code.ShouldBe(BillingErrorCodes.ApprovalSubjectChanged);
        var checker = Guid.CreateVersion7();
        refund.DecideRetry(true, checker, refund.RetrySubjectHash, approval, Now).IsSuccess.ShouldBeTrue();

        (refund.Status, refund.PayoutAttempt, refund.BankAccountId, refund.PaymentId, refund.RetryPending, refund.RetryDecidedBy)
            .ShouldBe((RefundStatus.Approved, 2, (Guid?)newAccount, (Guid?)null, false, (Guid?)checker));
        var granted = refund.DomainEvents.OfType<RefundPayoutRetryGranted>().Single();
        (granted.Attempt, granted.BankAccountId, granted.Amount, granted.PayerPartyId).ShouldBe((2, newAccount, 50m, refund.PayerPartyId));

        // A redelivered grant changes nothing; the old payout's events no longer apply; the new payout links and pays it.
        refund.RetryGrantedWith(approval).ShouldBeTrue();
        refund.MarkPaymentFailed(failed, FailedRef, "late redelivery").ShouldBeFalse();
        refund.LinkPayment(failed);
        refund.PaymentId.ShouldBeNull();
        var next = Guid.CreateVersion7();
        refund.LinkPayment(next);
        refund.MarkPaid(next).IsSuccess.ShouldBeTrue();
        refund.Status.ShouldBe(RefundStatus.Paid);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void APayoutRetryNotGranted_LeavesTheRefundPaymentFailed_WithTheRequestCleared_DE008(bool rejectedByAChecker)
    {
        var (refund, _, _) = FailedRefund();
        var originalAccount = refund.BankAccountId;
        refund.RequestPayoutRetry(FailedRef, "retry", Guid.CreateVersion7(), new string('b', 64), Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        var checker = rejectedByAChecker ? Guid.CreateVersion7() : Guid.Empty;

        refund.DecideRetry(false, checker, refund.RetrySubjectHash, Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();

        (refund.Status, refund.RetryPending, refund.RetryBankAccountId, refund.PayoutAttempt, refund.BankAccountId)
            .ShouldBe((RefundStatus.PaymentFailed, false, (Guid?)null, 1, originalAccount));
        refund.DomainEvents.OfType<RefundPayoutRetryGranted>().ShouldBeEmpty();
        refund.DecideRetry(false, checker, "whatever", Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue("a redelivered ending changes nothing");
        refund.RequestPayoutRetry(FailedRef, "retry again", Guid.CreateVersion7(), "h", Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue("it may be asked again");
    }

    [Fact]
    public void ACancelledRefund_IsOnlyOneWhosePayoutFailed_WithTheReferenceTyped_AndAbandonsThePayout_DE008()
    {
        var (refund, _, _) = FailedRefund();
        var person = Guid.CreateVersion7();
        refund.Cancel("PAY-WRONG", "Payer asked to keep it as credit", person, Now).Error!.Code.ShouldBe(ErrorCodes.TypedReferenceMismatch);

        refund.RequestPayoutRetry(FailedRef, "retry", Guid.CreateVersion7(), "h", Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        refund.Cancel(FailedRef, "Payer asked to keep it as credit", person, Now).Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
        refund.DecideRetry(false, Guid.Empty, refund.RetrySubjectHash, Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();

        refund.Cancel(FailedRef, "Payer asked to keep it as credit", person, Now).IsSuccess.ShouldBeTrue();
        (refund.Status, refund.CancelReason, refund.CancelledBy).ShouldBe((RefundStatus.Cancelled, "Payer asked to keep it as credit", (Guid?)person));
        refund.DomainEvents.OfType<RefundCancelled>().Single().Reason.ShouldBe("Payer asked to keep it as credit");
        refund.Cancel(FailedRef, "again", person, Now).Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
        refund.RequestPayoutRetry(FailedRef, "retry", Guid.CreateVersion7(), "h", Guid.CreateVersion7(), Now).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void ACreditNote_SaysWhatItsInvoiceStillAsksFor_DE009()
    {
        var (_, invoice) = Issued(300m);
        invoice.ApplyAllocation(100m, Now);
        var reason = new AdjustmentReason("GOODWILL", false);

        var part = invoice.RaiseCreditNote(Guid.CreateVersion7(), "CN-", 50m, reason, Guid.CreateVersion7(), Start, Now).Value;
        part.DomainEvents.OfType<CreditNoteIssued>().Single().InvoiceOutstanding.ShouldBe(150m);
        var rest = invoice.RaiseCreditNote(Guid.CreateVersion7(), "CN-", 150m, reason, Guid.CreateVersion7(), Start, Now).Value;
        rest.DomainEvents.OfType<CreditNoteIssued>().Single().InvoiceOutstanding.ShouldBe(0m);
    }

    [Fact]
    public void ACreditNoteWhoseReasonNeedsApproval_WaitsForIt_AndCreditsAtMostWhatIsStillOutstanding_DE010_OQ058()
    {
        var (_, invoice) = Issued(300m);
        var maker = Guid.CreateVersion7();
        var reason = new AdjustmentReason("WRITE_OFF", true);
        invoice.RequestCreditNote(Guid.CreateVersion7(), 300.01m, reason, maker, Now).Error!.Code.ShouldBe(BillingErrorCodes.CreditExceedsInvoice);

        var request = invoice.RequestCreditNote(Guid.CreateVersion7(), 200m, reason, maker, Now).Value;
        request.Status.ShouldBe(CreditNoteRequestStatus.Pending);
        invoice.DomainEvents.OfType<CreditNoteApprovalRequested>().Single().ShouldSatisfyAllConditions(
            e => e.RequestId.ShouldBe(request.Id), e => e.Amount.ShouldBe(200m), e => e.SubjectHash.ShouldBe(request.SubjectHash));
        (invoice.Credited, invoice.Outstanding).ShouldBe((0m, 300m), "nothing is credited while it waits");

        // The invoice isn't frozen: a payment is allocated while the request waits.
        invoice.ApplyAllocation(150m, Now).ShouldBe(150m);

        var approval = Guid.CreateVersion7();
        invoice.DecideCreditNote(request.Id, true, maker, request.SubjectHash, approval, "GRANTED", Guid.CreateVersion7(), "CN-", Start, Now).Error!.Code
            .ShouldBe(ErrorCodes.MakerCheckerSamePerson);
        invoice.DecideCreditNote(request.Id, true, Guid.CreateVersion7(), "another subject", approval, "GRANTED", Guid.CreateVersion7(), "CN-", Start, Now).Error!.Code
            .ShouldBe(BillingErrorCodes.ApprovalSubjectChanged);
        var checker = Guid.CreateVersion7();
        var credit = invoice.DecideCreditNote(request.Id, true, checker, request.SubjectHash, approval, "GRANTED", Guid.CreateVersion7(), "CN-", Start, Now).Value!;

        credit.TotalAmount.ShouldBe(150m, "only what was still outstanding, never the 200 asked");
        (request.Status, request.CreditNoteId, request.CreditedAmount, request.DecidedBy).ShouldBe((CreditNoteRequestStatus.Issued, (Guid?)credit.Id, (decimal?)150m, (Guid?)checker));
        (invoice.Status, invoice.Outstanding).ShouldBe((InvoiceStatus.Credited, 0m));
        invoice.Adjustments.Single().ApprovedBy.ShouldBe(checker);
        credit.DomainEvents.OfType<CreditNoteIssued>().Single().InvoiceOutstanding.ShouldBe(0m);

        // A redelivered grant changes nothing.
        invoice.DecideCreditNote(request.Id, true, checker, request.SubjectHash, approval, "GRANTED", Guid.CreateVersion7(), "CN-", Start, Now).Value.ShouldBeNull();
        invoice.Adjustments.Count.ShouldBe(1);
    }

    [Fact]
    public void ACreditNoteApprovedWhenNothingIsOutstanding_Lapses_AndCreditsNothing_DE010()
    {
        var (_, invoice) = Issued(300m);
        var request = invoice.RequestCreditNote(Guid.CreateVersion7(), 100m, new AdjustmentReason("WRITE_OFF", true), Guid.CreateVersion7(), Now).Value;
        invoice.ApplyAllocation(300m, Now);

        invoice.DecideCreditNote(request.Id, true, Guid.CreateVersion7(), request.SubjectHash, Guid.CreateVersion7(), "GRANTED", Guid.CreateVersion7(), "CN-", Start, Now)
            .Value.ShouldBeNull();

        (request.Status, request.CreditedAmount, request.CreditNoteId).ShouldBe((CreditNoteRequestStatus.Lapsed, (decimal?)0m, (Guid?)null));
        (invoice.Status, invoice.Credited).ShouldBe((InvoiceStatus.Paid, 0m));
    }

    [Theory]
    [InlineData("REJECTED", true)]
    [InlineData("EXPIRED", false)]
    [InlineData("WITHDRAWN", false)]
    public void ACreditNoteRequestNotGranted_IsCleared_AndCreditsNothing_DE010(string ending, bool byAChecker)
    {
        var (_, invoice) = Issued(300m);
        var request = invoice.RequestCreditNote(Guid.CreateVersion7(), 100m, new AdjustmentReason("WRITE_OFF", true), Guid.CreateVersion7(), Now).Value;
        var checker = byAChecker ? Guid.CreateVersion7() : Guid.Empty;

        invoice.DecideCreditNote(request.Id, false, checker, request.SubjectHash, Guid.CreateVersion7(), ending, Guid.CreateVersion7(), "CN-", Start, Now).Value.ShouldBeNull();

        (request.Status, request.Ending, request.DecidedBy).ShouldBe((CreditNoteRequestStatus.Rejected, ending, byAChecker ? checker : null));
        (invoice.Credited, invoice.Outstanding).ShouldBe((0m, 300m));
        invoice.DecideCreditNote(request.Id, true, Guid.CreateVersion7(), request.SubjectHash, Guid.CreateVersion7(), "GRANTED", Guid.CreateVersion7(), "CN-", Start, Now)
            .IsFailure.ShouldBeTrue("a cleared request takes no later grant");
    }
}
