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

    private static (BillingAccount Account, Invoice Invoice) Issued(decimal instalment = 300m)
    {
        var account = BillingAccount.OpenAccount(Guid.CreateVersion7(), Guid.CreateVersion7(), "ZAR", "DEBIT_ORDER", null, Now);
        var schedule = account.CreateSchedule("GENERAL", Guid.CreateVersion7(), "MOTOR-v1", Monthly, Start, null, instalment).Value;
        var due = account.DueInstalments(Start).Select(d => d.Instalment).ToList();
        var invoice = Invoice.IssueInvoice(Guid.CreateVersion7(), "INV-", account, schedule, due, "DEBIT_ORDER", Start, Now).Value;
        return (account, invoice);
    }

    [Fact]
    public void RefundRequest_Lifecycle_MatchesDomainModel()
    {
        var specified = DomainModelSpec.Transitions("billing", "RefundRequest").Select(t => t.Replace("Payment succeeded", "PaymentSucceeded", StringComparison.Ordinal))
            .OrderBy(t => t, StringComparer.Ordinal).ToList();
        RefundRequest.Lifecycle.Transitions.Select(t => $"{t.From}-{t.Trigger}->{t.To}").OrderBy(t => t, StringComparer.Ordinal).ToList().ShouldBe(specified);
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
    public void Invoice_PastItsDueDate_IsOverdue()
    {
        var (_, invoice) = Issued();
        invoice.MarkOverdue(Start).ShouldBeFalse();
        invoice.MarkOverdue(Start.AddDays(1)).ShouldBeTrue();
        invoice.Status.ShouldBe(InvoiceStatus.Overdue);
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
}
