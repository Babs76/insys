/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Health.Tests
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
using Insys.Health.Domain.Payments;
using Shouldly;

namespace Insys.Health.Tests;

/// <summary>
/// DE-009 (Ade, 24 November 2026): a Billing credit note on a recovery's invoice. Partly credited, the balance falls by the credit and it stays INVOICED;
/// fully credited, the invoice is withdrawn, what it collected is settled and the rest stays owed, OPEN again. No amount is lost.
/// </summary>
public sealed class RecoveryInvoiceCreditTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 24);
    private static readonly Guid Invoice = Guid.CreateVersion7();

    private static ProviderRecovery Invoiced(decimal amount = 1_000m)
    {
        var recovery = ProviderRecovery.Raise(Guid.CreateVersion7(), "RC1", RecoveryDebtors.Provider, "PR-1", Guid.CreateVersion7(), RecoverySources.Reversal, Guid.CreateVersion7(),
            Guid.CreateVersion7(), "MC1", amount, "ZAR", false, "BILLED_IN_ERROR", Today, Today.AddDays(90), "EFT", Now);
        recovery.InvoiceIssued(Invoice);
        recovery.Status.ShouldBe(RecoveryStatuses.Invoiced);
        return recovery;
    }

    [Fact]
    public void APartialCredit_LowersTheBalance_KeepsItInvoiced_AndIsInItsHistory_DE009()
    {
        var recovery = Invoiced();
        recovery.CreditInvoice(Guid.CreateVersion7(), "CN-1", Invoice, 200m, 800m, Today, Now).ShouldBeTrue();

        (recovery.Status, recovery.Balance, recovery.InvoiceId).ShouldBe((RecoveryStatuses.Invoiced, 800m, (Guid?)Invoice));
        var credit = recovery.Credits.Single();
        (credit.Amount, credit.Outcome, credit.BalanceBefore, credit.BalanceAfter, credit.SettledAmount).ShouldBe((200m, RecoveryCreditOutcomes.Partial, 1_000m, 800m, 0m));
        recovery.InvoiceSettled(Today, Now);
        recovery.DomainEvents.OfType<ProviderRecoverySettled>().Single().Amount.ShouldBe(800m, "settled with what the invoice still asked");
    }

    [Fact]
    public void AFullCredit_WithdrawsTheInvoice_TheRecoveryIsOpenWithWhatIsStillOwed_AndCanBeWrittenOffOrReversed_DE009()
    {
        var recovery = Invoiced();
        recovery.CreditInvoice(Guid.CreateVersion7(), "CN-1", Invoice, 1_000m, 0m, Today, Now).ShouldBeTrue();

        (recovery.Status, recovery.Balance, recovery.InvoiceId, recovery.InReview).ShouldBe((RecoveryStatuses.Open, 1_000m, (Guid?)null, true));
        recovery.Credits.Single().Outcome.ShouldBe(RecoveryCreditOutcomes.Withdrawn);
        recovery.DomainEvents.OfType<ProviderRecoverySettled>().ShouldBeEmpty("nothing was collected");
        recovery.DomainEvents.OfType<ProviderRecoveryReviewDue>().Count().ShouldBe(1, "a person decides");
        recovery.Invoice(Today, null, Now).Error!.Code.ShouldBe(BuildingBlocks.Domain.ErrorCodes.InvalidStateTransition, "TODO(spec): not invoiced again");
        recovery.RequestWriteOff("Provider closed", Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        recovery.DomainEvents.OfType<ProviderRecoveryWriteOffRequested>().Single().Amount.ShouldBe(1_000m);

        var reversed = Invoiced();
        reversed.CreditInvoice(Guid.CreateVersion7(), "CN-2", Invoice, 1_000m, 0m, Today, Now);
        reversed.Reverse("Raised in error", Today, Now).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void AFullCreditAfterPartPayment_SettlesWhatWasPaid_AndKeepsTheRestOwed_NeverLosingAnAmount_DE009()
    {
        var recovery = Invoiced();
        recovery.CreditInvoice(Guid.CreateVersion7(), "CN-1", Invoice, 100m, 900m, Today, Now);
        // 300 paid on the invoice, then the remaining 600 credited: nothing outstanding.
        recovery.CreditInvoice(Guid.CreateVersion7(), "CN-2", Invoice, 600m, 0m, Today, Now).ShouldBeTrue();

        recovery.DomainEvents.OfType<ProviderRecoverySettled>().Single().Amount.ShouldBe(300m);
        (recovery.Status, recovery.Balance).ShouldBe((RecoveryStatuses.Open, 600m));
        var withdrawn = recovery.Credits.Last();
        (withdrawn.SettledAmount, withdrawn.BalanceBefore, withdrawn.BalanceAfter).ShouldBe((300m, 900m, 600m));
        (recovery.Credits.Where(c => c.Outcome == RecoveryCreditOutcomes.Partial).Sum(c => c.Amount) + withdrawn.SettledAmount + recovery.Balance)
            .ShouldBe(recovery.OriginalAmount, "credited + settled + still owed = the original");
    }

    [Fact]
    public void ACredit_IsTakenOnce_AndOnlyByAnInvoicedRecoveryForItsOwnInvoice_DE009()
    {
        var recovery = Invoiced();
        var note = Guid.CreateVersion7();
        recovery.CreditInvoice(note, "CN-1", Invoice, 200m, 800m, Today, Now).ShouldBeTrue();
        recovery.CreditInvoice(note, "CN-1", Invoice, 200m, 800m, Today, Now).ShouldBeFalse();
        recovery.CreditInvoice(Guid.CreateVersion7(), "CN-X", Guid.CreateVersion7(), 50m, 750m, Today, Now).ShouldBeFalse("another invoice");
        recovery.Balance.ShouldBe(800m);

        var open = ProviderRecovery.Raise(Guid.CreateVersion7(), "RC2", RecoveryDebtors.Provider, "PR-1", Guid.CreateVersion7(), RecoverySources.Reversal, Guid.CreateVersion7(),
            Guid.CreateVersion7(), "MC2", 500m, "ZAR", true, "BILLED_IN_ERROR", Today, Today.AddDays(90), "EFT", Now);
        open.CreditInvoice(Guid.CreateVersion7(), "CN-2", Invoice, 100m, 0m, Today, Now).ShouldBeFalse();
        (open.Status, open.Balance).ShouldBe((RecoveryStatuses.Open, 500m));
    }
}
