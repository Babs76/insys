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
using Insys.Testing;

namespace Insys.Billing.Tests;

public sealed class BillingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Start = new(2026, 11, 1);
    private static readonly BillingFrequency Monthly = new("MONTHLY", 12);

    private static readonly BillingFrequency Quarterly = new("QUARTERLY", 4);

    private static (BillingAccount Account, Invoice Invoice) Issued(decimal instalment = 300m)
    {
        var account = BillingAccount.OpenAccount(Guid.CreateVersion7(), Guid.CreateVersion7(), "ZAR", "DEBIT_ORDER", null, Now);
        var schedule = account.CreateSchedule("GENERAL", Guid.CreateVersion7(), "MOTOR-v1", Monthly, Start, null, instalment).Value;
        var due = account.DueInstalments(Start).Select(d => d.Instalment).ToList();
        var invoice = Invoice.IssueInvoice(Guid.CreateVersion7(), "INV-", account, schedule, due, "DEBIT_ORDER", Start, Now).Value;
        return (account, invoice);
    }

    private static Invoice Taxed(decimal instalment, decimal rate = 15m)
    {
        var account = BillingAccount.OpenAccount(Guid.CreateVersion7(), Guid.CreateVersion7(), "ZAR", "DEBIT_ORDER", null, Now);
        var schedule = account.CreateSchedule("GENERAL", Guid.CreateVersion7(), "MOTOR-v1", Monthly, Start, null, instalment).Value;
        return Invoice.IssueInvoice(Guid.CreateVersion7(), "INV-", account, schedule, account.DueInstalments(Start).Select(d => d.Instalment).ToList(), "DEBIT_ORDER", Start, Now,
            tax: new InvoiceTax("VAT", rate)).Value;
    }

    [Fact]
    public void AnInvoicesTax_IsTakenOutOfThePremium_SoWhatIsOwedDoesNotChange_OQ058()
    {
        var invoice = Taxed(115m);
        (invoice.TotalAmount, invoice.TaxAmount, invoice.NetAmount).ShouldBe((115m, 15m, 100m));
        (invoice.Lines.Single().TaxCode, invoice.Lines.Single().TaxAmount, invoice.Lines.Single().Amount).ShouldBe(("VAT", 15m, 115m));
        invoice.DomainEvents.OfType<InvoiceIssued>().Single().ShouldSatisfyAllConditions(e => e.TaxAmount.ShouldBe(15m), e => e.TaxCode.ShouldBe("VAT"), e => e.TotalAmount.ShouldBe(115m));
    }

    [Fact]
    public void ATaxFreeProduct_IsUnchanged_OQ156()
    {
        var (_, invoice) = Issued(300m);
        (invoice.TotalAmount, invoice.TaxAmount, invoice.NetAmount).ShouldBe((300m, 0m, 300m));
        invoice.Lines.Single().TaxCode.ShouldBeNull();
    }

    [Theory]
    [InlineData(100, 15, 13.04)]
    [InlineData(99.99, 15, 13.04)]
    [InlineData(0.05, 15, 0.01)]
    public void TheTaxIncluded_IsRoundedToTheCent(decimal amount, decimal rate, decimal tax) => new InvoiceTax("VAT", rate).IncludedIn(amount).ShouldBe(tax);

    [Fact]
    public void ACreditNote_TakesItsShareOfTheTax_AndCreditsAddUpToTheInvoicesTaxExactly_OQ058()
    {
        var invoice = Taxed(100m); // tax 13.04
        var reason = new AdjustmentReason("ERROR", false);
        var first = invoice.RaiseCreditNote(Guid.CreateVersion7(), "CN-", 33.33m, reason, Guid.CreateVersion7(), Start, Now).Value;
        var second = invoice.RaiseCreditNote(Guid.CreateVersion7(), "CN-", 33.33m, reason, Guid.CreateVersion7(), Start, Now).Value;
        var last = invoice.RaiseCreditNote(Guid.CreateVersion7(), "CN-", 33.34m, reason, Guid.CreateVersion7(), Start, Now).Value;
        (first.TaxAmount, first.NetAmount).ShouldBe((4.35m, 28.98m));
        second.TaxAmount.ShouldBe(4.35m);
        last.TaxAmount.ShouldBe(4.34m);
        (first.TaxAmount + second.TaxAmount + last.TaxAmount).ShouldBe(invoice.TaxAmount);
        last.DomainEvents.OfType<CreditNoteIssued>().Single().ShouldSatisfyAllConditions(e => e.TaxAmount.ShouldBe(4.34m), e => e.TaxCode.ShouldBe("VAT"));
    }

    [Fact]
    public void RefundRequest_Lifecycle_MatchesDomainModel()
    {
        var specified = DomainModelSpec.Transitions("billing", "RefundRequest").Select(t => t.Replace("Payment succeeded", "PaymentSucceeded", StringComparison.Ordinal))
            .OrderBy(t => t, StringComparer.Ordinal).ToList();
        RefundRequest.Lifecycle.Transitions.Select(t => $"{t.From}-{t.Trigger}->{t.To}").OrderBy(t => t, StringComparer.Ordinal).ToList().ShouldBe(specified);
    }

    /// <summary>
    /// A General policy's schedule is a share of its annual premium: twelve monthly instalments of 3 050.00 total exactly
    /// 3 050.00, eleven of 254.17 and a last of 254.13. Amended mid-term, the regenerated instalments total their share of
    /// the new annual premium, and the year still adds up to what was invoiced plus that share (Ade, 28 October 2026).
    /// </summary>
    /// <summary>OQ-132 (b): a schedule extended for a non-renewal's notice runs to the new end, with the extra days' premium as one instalment due after the old end.</summary>
    [Fact]
    public void AnExtendedSchedule_BillsTheExtraDaysAsOneInstalment_AfterItsOldEnd_OQ132()
    {
        var account = BillingAccount.OpenAccount(Guid.CreateVersion7(), Guid.CreateVersion7(), "ZAR", "DEBIT_ORDER", null, Now);
        var end = Start.AddYears(1).AddDays(-1);
        var schedule = account.CreateSchedule("GENERAL", Guid.CreateVersion7(), "MOTOR-v1", Monthly, Start, end, 254.17m, 3_050m).Value;
        account.ExtendSchedule(schedule.Id, end, 175.48m).IsFailure.ShouldBeTrue("an extension is past the end");
        account.ExtendSchedule(schedule.Id, end.AddDays(21), 175.48m).IsSuccess.ShouldBeTrue();
        schedule.EndDate.ShouldBe(end.AddDays(21));
        schedule.Instalments.Count.ShouldBe(13);
        (schedule.Instalments[^1].DueDate, schedule.Instalments[^1].Amount).ShouldBe((end.AddDays(1), 175.48m));
    }

    [Fact]
    public void AMonthlyScheduleOfAnAnnualPremium_TotalsItExactly_TheLastInstalmentTakingTheRounding()
    {
        var account = BillingAccount.OpenAccount(Guid.CreateVersion7(), Guid.CreateVersion7(), "ZAR", "DEBIT_ORDER", null, Now);
        var schedule = account.CreateSchedule("GENERAL", Guid.CreateVersion7(), "MOTOR-v1", Monthly, Start, Start.AddYears(1).AddDays(-1), 254.17m, 3_050m).Value;

        var instalments = schedule.Instalments.OrderBy(i => i.DueDate).ToList();
        instalments.Count.ShouldBe(12);
        instalments.Sum(i => i.Amount).ShouldBe(3_050.00m);
        instalments.Take(11).ShouldAllBe(i => i.Amount == 254.17m);
        instalments[^1].Amount.ShouldBe(254.13m);

        // From the seventh month the annual premium is 4 000.00 (333.33 a month): the six regenerated instalments total half of it.
        account.AmendSchedule(schedule.Id, Monthly, 333.33m, Start.AddMonths(6), 4_000m).IsSuccess.ShouldBeTrue();
        var live = schedule.Instalments.Where(i => i.Status != InstalmentStatus.Cancelled).OrderBy(i => i.DueDate).ToList();
        live.Count.ShouldBe(12);
        live.Skip(6).Sum(i => i.Amount).ShouldBe(2_000.00m);
        live[^1].Amount.ShouldBe(333.35m);
        live.Sum(i => i.Amount).ShouldBe(6 * 254.17m + 2_000.00m);

        // A schedule of a fixed instalment (Life, Health) is unchanged.
        var fixedSchedule = account.CreateSchedule("LIFE", Guid.CreateVersion7(), "FUN-v2", Monthly, Start, Start.AddYears(1).AddDays(-1), 254.17m).Value;
        fixedSchedule.Instalments.ShouldAllBe(i => i.Amount == 254.17m);
    }

    /// <summary>
    /// A schedule starting late in a month keeps its day: from 30 October, twelve monthly instalments on the 30th (the
    /// 28th in February, as the month allows), totalling the annual premium; never a thirteenth on 28 October.
    /// </summary>
    [Fact]
    public void AMonthlyScheduleStartingOnThe30th_KeepsItsDay_AndHasTwelveInstalmentsAYear()
    {
        var account = BillingAccount.OpenAccount(Guid.CreateVersion7(), Guid.CreateVersion7(), "ZAR", "DEBIT_ORDER", null, Now);
        var start = new DateOnly(2026, 10, 30);
        var schedule = account.CreateSchedule("GENERAL", Guid.CreateVersion7(), "MOTOR-v1", Monthly, start, start.AddYears(1).AddDays(-1), 254.17m, 3_050m).Value;

        var dates = schedule.Instalments.OrderBy(i => i.DueDate).Select(i => i.DueDate).ToList();
        dates.Count.ShouldBe(12);
        dates[4].ShouldBe(new DateOnly(2027, 2, 28));
        dates[5].ShouldBe(new DateOnly(2027, 3, 30));
        dates[^1].ShouldBe(new DateOnly(2027, 9, 30));
        schedule.Instalments.Sum(i => i.Amount).ShouldBe(3_050.00m);

        var quarterly = account.CreateSchedule("LIFE", Guid.CreateVersion7(), "FUN-v2", Quarterly, new DateOnly(2026, 11, 30), new DateOnly(2027, 11, 29), 300m).Value;
        quarterly.Instalments.Select(i => i.DueDate.Day).ShouldBe([30, 28, 30, 30]);
    }

    [Fact]
    public void Schedule_GeneratesInstalments_BIL001()
    {
        var account = BillingAccount.OpenAccount(Guid.CreateVersion7(), Guid.CreateVersion7(), "ZAR", "DEBIT_ORDER", null, Now);
        var schedule = account.CreateSchedule("LIFE", Guid.CreateVersion7(), "FUN-v2", Monthly, Start, new DateOnly(2027, 4, 30), 150m).Value;

        schedule.Instalments.Count.ShouldBe(6);
        schedule.Instalments.ShouldAllBe(i => i.Amount == 150m);
        account.CreateSchedule("LIFE", schedule.PolicyRef, "FUN-v2", Monthly, Start, null, 150m).Error!.Code.ShouldBe(BillingErrorCodes.ScheduleExists);
    }

    [Fact]
    public void Invoice_IsSettledByAllocations_AndNeverOverApplied_BIL003()
    {
        var (_, invoice) = Issued(300m);
        invoice.TotalAmount.ShouldBe(300m);

        invoice.ApplyAllocation(100m, Now).ShouldBe(100m);
        invoice.Status.ShouldBe(InvoiceStatus.PartPaid);
        invoice.ApplyAllocation(500m, Now).ShouldBe(200m);
        invoice.Status.ShouldBe(InvoiceStatus.Paid);
        invoice.DomainEvents.OfType<InvoiceSettled>().ShouldHaveSingleItem();
        invoice.ApplyAllocation(1m, Now).ShouldBe(0m);
    }

    [Fact]
    public void ReversedAllocation_ReopensTheInvoice()
    {
        var (_, invoice) = Issued(300m);
        invoice.ApplyAllocation(300m, Now);

        invoice.ReverseAllocation(300m, Start);

        invoice.Status.ShouldBe(InvoiceStatus.Issued);
        invoice.Outstanding.ShouldBe(300m);
    }

    [Fact]
    public void CreditNote_IsANewDocument_TheInvoiceIsNotOverwritten_BIL004()
    {
        var (_, invoice) = Issued(300m);

        var credit = invoice.RaiseCreditNote(Guid.CreateVersion7(), "CN-", 100m, new AdjustmentReason("GOODWILL", false), Guid.CreateVersion7(), Start, Now).Value;

        credit.DocumentType.ShouldBe(DocumentType.CreditNote);
        credit.ReversesInvoiceId.ShouldBe(invoice.Id);
        invoice.TotalAmount.ShouldBe(300m);
        invoice.Outstanding.ShouldBe(200m);
        invoice.RaiseCreditNote(Guid.CreateVersion7(), "CN-", 200.01m, new AdjustmentReason("GOODWILL", false), Guid.CreateVersion7(), Start, Now).Error!.Code
            .ShouldBe(BillingErrorCodes.CreditExceedsInvoice);
        invoice.RaiseCreditNote(Guid.CreateVersion7(), "CN-", 200m, new AdjustmentReason("GOODWILL", false), Guid.CreateVersion7(), Start, Now).IsSuccess.ShouldBeTrue();
        invoice.Status.ShouldBe(InvoiceStatus.Credited);
    }

    [Fact]
    public void Invoice_PastItsDueDate_IsOverdue_AndSaysSo()
    {
        var (_, invoice) = Issued();
        invoice.MarkOverdue(Start, Now).ShouldBeFalse();
        invoice.MarkOverdue(Start.AddDays(1), Now).ShouldBeTrue();
        invoice.Status.ShouldBe(InvoiceStatus.Overdue);
        invoice.DomainEvents.OfType<InvoiceOverdue>().Single().DueDate.ShouldBe(Start);
        invoice.MarkOverdue(Start.AddDays(2), Now).ShouldBeFalse("only the transition is reported");
    }

    [Fact]
    public void Invoice_IssuedAfterItsInstalmentFellDue_IsDueOnTheInstalmentsDate_UnlessBilledForTheFirstTime_Request46()
    {
        var account = BillingAccount.OpenAccount(Guid.CreateVersion7(), Guid.CreateVersion7(), "ZAR", "DEBIT_ORDER", null, Now);
        var schedule = account.CreateSchedule("GENERAL", Guid.CreateVersion7(), "MOTOR-v1", Monthly, Start, null, 300m).Value;
        var due = account.DueInstalments(Start.AddDays(1)).Select(d => d.Instalment).ToList();

        // The daily run missed the due date: the premium is still due from its own date, so the next run finds it overdue.
        Invoice.IssueInvoice(Guid.CreateVersion7(), "INV-", account, schedule, due, "DEBIT_ORDER", Start.AddDays(1), Now).Value.DueDate.ShouldBe(Start);

        // A backdated policy billed for the first time: its back premiums are due from the day they are invoiced.
        Invoice.IssueInvoice(Guid.CreateVersion7(), "INV-", account, schedule, due, "DEBIT_ORDER", Start.AddDays(1), Now, dueNoEarlierThan: Start.AddDays(1)).Value
            .DueDate.ShouldBe(Start.AddDays(1));
    }

    [Fact]
    public void Refund_NeedsASecondPerson_BeforeItIsPaid_SRS002()
    {
        var (account, _) = Issued();
        var maker = Guid.CreateVersion7();
        var bankAccount = Guid.CreateVersion7();
        var refund = RefundRequest.RequestRefund(Guid.CreateVersion7(), account, 50m, "OVERPAID", bankAccount, new string('a', 64), maker, Now).Value;
        refund.DomainEvents.OfType<RefundRequested>().Single().SubjectHash.ShouldBe(refund.SubjectHash);

        refund.Decide(true, maker, refund.SubjectHash, Now).Error!.Code.ShouldBe(ErrorCodes.MakerCheckerSamePerson);
        refund.Decide(true, Guid.CreateVersion7(), "another subject", Now).Error!.Code.ShouldBe(BillingErrorCodes.ApprovalSubjectChanged);
        refund.Status.ShouldBe(RefundStatus.Requested);
        refund.MarkPaid(Guid.CreateVersion7()).IsFailure.ShouldBeTrue();

        refund.Decide(true, Guid.CreateVersion7(), refund.SubjectHash, Now).IsSuccess.ShouldBeTrue();
        refund.DomainEvents.OfType<RefundApproved>().Single().BankAccountId.ShouldBe(bankAccount);
        refund.MarkPaid(Guid.CreateVersion7()).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void ARefundApprovedByTheChecker_IsGrantedByThem_AndAChangedOneIsNot_D5()
    {
        var (account, _) = Issued();
        var refund = RefundRequest.RequestRefund(Guid.CreateVersion7(), account, 50m, "OVERPAID", Guid.CreateVersion7(), new string('a', 64), Guid.CreateVersion7(), Now)
            .Value;
        var checker = Guid.CreateVersion7();

        refund.Decide(true, checker, "the subject before a change", Now).Error!.Code.ShouldBe(BillingErrorCodes.ApprovalSubjectChanged);
        refund.GrantedBy(checker).ShouldBeFalse();
        refund.Decide(true, checker, refund.SubjectHash, Now).IsSuccess.ShouldBeTrue();
        (refund.GrantedBy(checker), refund.GrantedBy(Guid.CreateVersion7())).ShouldBe((true, false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RefundEnding_RejectsItForGood_WhetherRejectedExpiredOrWithdrawn_SRS002(bool rejectedByAChecker)
    {
        var (account, _) = Issued();
        var maker = Guid.CreateVersion7();
        var refund = RefundRequest.RequestRefund(Guid.CreateVersion7(), account, 50m, "OVERPAID", Guid.CreateVersion7(), new string('a', 64), maker, Now).Value;
        var checker = rejectedByAChecker ? Guid.CreateVersion7() : Guid.Empty;

        refund.Decide(false, maker, refund.SubjectHash, Now).Error!.Code.ShouldBe(ErrorCodes.MakerCheckerSamePerson);
        refund.Decide(false, checker, "another subject", Now).Error!.Code.ShouldBe(BillingErrorCodes.ApprovalSubjectChanged);
        refund.Decide(false, checker, refund.SubjectHash, Now).IsSuccess.ShouldBeTrue();
        (refund.Status, refund.DecidedBy).ShouldBe((RefundStatus.Rejected, rejectedByAChecker ? checker : null));

        // Applied again (a redelivery), it changes nothing; an approval no longer applies.
        refund.Decide(false, checker, refund.SubjectHash, Now).IsSuccess.ShouldBeTrue();
        refund.Decide(true, Guid.CreateVersion7(), refund.SubjectHash, Now).IsFailure.ShouldBeTrue();
        refund.DomainEvents.OfType<RefundApproved>().ShouldBeEmpty();
    }
}
