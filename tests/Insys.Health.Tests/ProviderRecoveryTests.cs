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
using Insys.Health.Domain;
using Insys.Health.Domain.Payments;
using Shouldly;

namespace Insys.Health.Tests;

/// <summary>Wave 5a §4.7 (F049, OQ-139, OQ-145, OQ-146): what is owed only falls by offsets and settlement, and it is never dropped.</summary>
public sealed class ProviderRecoveryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 1);

    private static ProviderRecovery Recovery(decimal amount = 500m, bool offset = true, string debtor = RecoveryDebtors.Provider) =>
        ProviderRecovery.Raise(Guid.CreateVersion7(), "RC1", debtor, debtor == RecoveryDebtors.Provider ? "PR-1" : null, Guid.CreateVersion7(), RecoverySources.Reversal, Guid.CreateVersion7(),
            Guid.CreateVersion7(), "MC1", amount, "ZAR", offset, "BILLED_IN_ERROR", Today, Today.AddDays(90), "EFT", Now);

    [Fact]
    public void WithTheFlagSet_ItIsOffset_WithoutIt_ItIsInvoicedAtOnce_OQ139()
    {
        var offset = Recovery();
        (offset.Basis, offset.Status, offset.ReviewDueOn).ShouldBe((RecoveryBases.Offset, RecoveryStatuses.Open, Today.AddDays(90)));
        offset.DomainEvents.OfType<ProviderRecoveryNoticeDue>().Count().ShouldBe(1);

        var invoiced = Recovery(offset: false);
        (invoiced.Basis, invoiced.Status).ShouldBe((RecoveryBases.Invoice, RecoveryStatuses.Invoiced));
        invoiced.DomainEvents.OfType<ProviderRecoveryInvoiceRequested>().Single().CollectionMethod.ShouldBe("EFT");
        invoiced.Offsettable.ShouldBeFalse();
    }

    [Fact]
    public void AMembersOverpayment_GoesToAPerson_AndIsNeverOffset_OQ145()
    {
        var member = Recovery(debtor: RecoveryDebtors.Member);
        (member.Basis, member.InReview, member.Offsettable).ShouldBe((RecoveryBases.Person, true, false));
        member.DomainEvents.OfType<ProviderRecoveryReviewDue>().Count().ShouldBe(1);
        member.DomainEvents.OfType<ProviderRecoveryNoticeDue>().ShouldBeEmpty();
    }

    [Fact]
    public void AnOffset_NeverExceedsTheBalanceOrTheRemittance_AndIsTakenOnlyWhenTheRunIsReleased_Gap2()
    {
        var recovery = Recovery();
        var (run, remittance) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        recovery.ReserveOffset(run, remittance, 200m).ShouldBe(200m, "no more than the remittance");
        recovery.Balance.ShouldBe(300m);
        recovery.DomainEvents.OfType<ProviderRecoveryOffsetTaken>().ShouldBeEmpty();
        recovery.TakeOffsets(run, Today, Now);
        recovery.DomainEvents.OfType<ProviderRecoveryOffsetTaken>().Single().Amount.ShouldBe(200m);
        recovery.TakeOffsets(run, Today, Now);
        recovery.DomainEvents.OfType<ProviderRecoveryOffsetTaken>().Count().ShouldBe(1, "a redelivery changes nothing");

        recovery.ReserveOffset(Guid.CreateVersion7(), remittance, 900m).ShouldBe(300m, "no more than the balance");
        recovery.Balance.ShouldBe(0m);
    }

    [Fact]
    public void ARejectedRun_GivesItsOffsetBack_D2()
    {
        var recovery = Recovery();
        var run = Guid.CreateVersion7();
        recovery.ReserveOffset(run, Guid.CreateVersion7(), 200m);
        recovery.ReleaseOffsets(run);
        recovery.Balance.ShouldBe(500m);
        recovery.Taken.ShouldBe(0m);
    }

    [Fact]
    public void FullyOffset_ItIsRecovered()
    {
        var recovery = Recovery();
        var run = Guid.CreateVersion7();
        recovery.ReserveOffset(run, Guid.CreateVersion7(), 500m);
        recovery.TakeOffsets(run, Today, Now);
        (recovery.Status, recovery.ClosedOn).ShouldBe((RecoveryStatuses.Recovered, Today));
    }

    [Fact]
    public void ADisputedRecovery_IsHeldFromOffsets_UntilItsOwnCaseCloses_OQ146()
    {
        var recovery = Recovery();
        var (dispute, other) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        recovery.HoldForDispute(dispute);
        recovery.ReserveOffset(Guid.CreateVersion7(), Guid.CreateVersion7(), 100m).ShouldBe(0m);
        recovery.Invoice(Today, "EFT", Now).IsFailure.ShouldBeTrue();
        recovery.SendToReview(Now);
        recovery.InReview.ShouldBeFalse("a disputed one waits for its dispute");
        recovery.ReleaseHold(other);
        recovery.Offsettable.ShouldBeFalse("only the case that holds it");
        recovery.ReleaseHold(dispute);
        recovery.Offsettable.ShouldBeTrue();
    }

    [Fact]
    public void AWriteOff_NeedsAReasonAndASecondPerson_AndARefusalLeavesItOwed_Addition3()
    {
        var recovery = Recovery();
        recovery.RequestWriteOff(" ", Guid.CreateVersion7(), Now).Error!.Code.ShouldBe(HealthErrorCodes.ReasonRequired);
        recovery.RequestWriteOff("Liquidated", Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        recovery.Balance.ShouldBe(500m, "still owed");
        recovery.Offsettable.ShouldBeFalse("not deducted while the write-off is being decided");

        recovery.DecideWriteOff(false, Guid.CreateVersion7(), Guid.CreateVersion7(), recovery.WriteOffHash, Today, Now).IsSuccess.ShouldBeTrue();
        (recovery.Status, recovery.Balance, recovery.Offsettable).ShouldBe((RecoveryStatuses.Open, 500m, true));

        recovery.RequestWriteOff("Liquidated", Guid.CreateVersion7(), Now);
        recovery.DecideWriteOff(true, Guid.CreateVersion7(), Guid.CreateVersion7(), "another subject", Today, Now).IsFailure.ShouldBeTrue();
        recovery.DecideWriteOff(true, Guid.CreateVersion7(), Guid.CreateVersion7(), recovery.WriteOffHash, Today, Now).IsSuccess.ShouldBeTrue();
        (recovery.Status, recovery.Balance).ShouldBe((RecoveryStatuses.WrittenOff, 0m));
        recovery.DomainEvents.OfType<ProviderRecoveryWrittenOff>().Single().Amount.ShouldBe(500m);
    }

    [Fact]
    public void ARecoveryRaisedInError_IsReversed_AndWhatWasTakenIsRepaidOnce()
    {
        var recovery = Recovery();
        var run = Guid.CreateVersion7();
        recovery.ReserveOffset(run, Guid.CreateVersion7(), 200m);
        recovery.TakeOffsets(run, Today, Now);
        recovery.Reverse("", Today, Now).IsFailure.ShouldBeTrue();
        recovery.Reverse("Wrong provider", Today, Now).IsSuccess.ShouldBeTrue();
        (recovery.Status, recovery.Balance, recovery.RepayAmount).ShouldBe((RecoveryStatuses.Reversed, 0m, 200m));

        var next = Guid.CreateVersion7();
        recovery.ClaimRepayment(next).ShouldBe(200m);
        recovery.ClaimRepayment(Guid.CreateVersion7()).ShouldBe(0m, "once");
        recovery.RepaymentNotReleased(next);
        recovery.ClaimRepayment(next).ShouldBe(200m, "a rejected run leaves it to repay");
        recovery.RepaymentReleased(next, Today, Now);
        recovery.DomainEvents.OfType<ProviderRecoveryRepaid>().Single().Amount.ShouldBe(200m);
    }

    [Fact]
    public void ARecoverysInvoice_SettlesIt_Once()
    {
        var recovery = Recovery(offset: false);
        recovery.InvoiceIssued(Guid.CreateVersion7());
        recovery.InvoiceSettled(Today, Now);
        recovery.InvoiceSettled(Today, Now);
        (recovery.Status, recovery.Balance).ShouldBe((RecoveryStatuses.Recovered, 0m));
        recovery.DomainEvents.OfType<ProviderRecoverySettled>().Single().Amount.ShouldBe(500m);
    }

    [Fact]
    public void AFinding_OwesNothingUntilASecondPersonApproves()
    {
        var finding = ProviderRecovery.ProposeFinding(Guid.CreateVersion7(), "RC2", "PR-1", Guid.CreateVersion7(), 300m, "ZAR", "Audit", Guid.CreateVersion7(), Today, Now);
        (finding.Status, finding.Offsettable).ShouldBe((RecoveryStatuses.Proposed, false));
        finding.DecideFinding(true, Guid.CreateVersion7(), Guid.CreateVersion7(), finding.FindingHash, true, Today, Today.AddDays(90), "EFT", Now).IsSuccess.ShouldBeTrue();
        (finding.Status, finding.Basis, finding.Balance).ShouldBe((RecoveryStatuses.Open, RecoveryBases.Offset, 300m));
    }

    [Fact]
    public void ACreditNote_NamesWhatItCredits_AndIsDecidedOnce_F048()
    {
        ProviderCreditNote.Submit(Guid.CreateVersion7(), "CN1", "PR-1", "X", null, null, 10m, "ZAR", "r", Guid.CreateVersion7(), Now).Error!.Code.ShouldBe(HealthErrorCodes.CreditNoteInvalid);
        var note = ProviderCreditNote.Submit(Guid.CreateVersion7(), "CN1", "PR-1", "X", Guid.CreateVersion7(), null, 10m, "ZAR", "r", Guid.CreateVersion7(), Now).Value;
        note.Decide(true, Guid.CreateVersion7(), Guid.CreateVersion7(), note.SubjectHash, Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        note.Decide(true, Guid.CreateVersion7(), Guid.CreateVersion7(), note.SubjectHash, null, Now).IsFailure.ShouldBeTrue();
    }
}
