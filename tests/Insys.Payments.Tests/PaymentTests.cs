/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Payments.Tests
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
using Insys.Payments.Domain.Payments;
using Insys.Testing;

namespace Insys.Payments.Tests;

public sealed class PaymentTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 21);

    private static Payment New(Direction direction = Direction.In, decimal amount = 100m) =>
        Payment.InitiatePayment(Guid.CreateVersion7(), "KEY-" + Guid.NewGuid(), direction, "PREMIUM", "EFT", "SIMULATOR", amount, "ZAR", null, "INVOICE", Guid.CreateVersion7(),
            Now).Value;

    [Fact]
    public void Payment_Lifecycle_MatchesDomainModel()
    {
        var specified = DomainModelSpec.Transitions("payments", "Payment")
            .Select(t => t.Replace("Sent to provider", "SentToProvider", StringComparison.Ordinal).Replace("Provider confirms", "ProviderConfirms", StringComparison.Ordinal)
                .Replace("Provider rejects", "ProviderRejects", StringComparison.Ordinal).Replace("No or ambiguous response", "NoOrAmbiguousResponse", StringComparison.Ordinal))
            .OrderBy(t => t, StringComparer.Ordinal).ToList();
        Payment.Lifecycle.Transitions.Select(t => $"{t.From}-{t.Trigger.ToString().Replace("ResolveUnknownSucceeded", "ResolveUnknown").Replace("ResolveUnknownFailed", "ResolveUnknown")}->{t.To}")
            .OrderBy(t => t, StringComparer.Ordinal).ToList().ShouldBe(specified);
    }

    [Fact]
    public void PayoutBatch_Lifecycle_MatchesDomainModel()
    {
        // D-1 (Wave 2 batch 1, V0056) adds "Built-RejectBatch->Rejected", not yet in domain-model.json.
        var specified = DomainModelSpec.Transitions("payments", "PayoutBatch").Select(t => t.Replace("All payments final", "AllPaymentsFinal", StringComparison.Ordinal))
            .Append("Built-RejectBatch->Rejected").OrderBy(t => t, StringComparer.Ordinal).ToList();
        PayoutBatch.Lifecycle.Transitions.Select(t => $"{t.From}-{t.Trigger}->{t.To}").OrderBy(t => t, StringComparer.Ordinal).ToList().ShouldBe(specified);
    }

    [Fact]
    public void UnknownOutcome_IsNeverSucceeded_PAY005()
    {
        var payment = New();
        payment.Sent(ProviderOutcome.Unknown, null, null, Today, Now).IsSuccess.ShouldBeTrue();

        payment.Status.ShouldBe(PaymentStatus.Unknown);
        payment.DomainEvents.OfType<PaymentConfirmed>().ShouldBeEmpty();
        payment.DomainEvents.OfType<PaymentOutcomeUnknown>().ShouldHaveSingleItem();
        payment.ValueDate.ShouldBeNull();

        // A later callback does not settle it either: only a person, from evidence, does.
        payment.RecordCallback(ProviderOutcome.Succeeded, null, Today, Now).IsSuccess.ShouldBeTrue();
        payment.Status.ShouldBe(PaymentStatus.Unknown);
        payment.AllocatePayment([(Guid.CreateVersion7(), 10m)], "OLDEST_FIRST", Now).IsFailure.ShouldBeTrue();
        payment.ReversePayment("no", Today, Now).IsFailure.ShouldBeTrue();

        payment.ResolveUnknown(true, "Bank statement line 42", Today, Now).IsSuccess.ShouldBeTrue();
        payment.Status.ShouldBe(PaymentStatus.Succeeded);
        payment.History.Last().Source.ShouldBe("OPERATOR");
    }

    [Fact]
    public void Timeout_MakesAPendingPaymentUnknown_NotSucceeded()
    {
        var payment = New();
        payment.Sent(ProviderOutcome.Pending, "EXT-1", null, Today, Now);

        payment.MarkUnknown(Today.AddDays(1), Now.AddDays(1)).IsSuccess.ShouldBeTrue();

        payment.Status.ShouldBe(PaymentStatus.Unknown);
        payment.DomainEvents.OfType<PaymentConfirmed>().ShouldBeEmpty();
    }

    [Fact]
    public void EveryStatusChange_IsKeptInHistory_PAY002()
    {
        var payment = New();
        payment.Sent(ProviderOutcome.Pending, "EXT-1", null, Today, Now);
        payment.RecordCallback(ProviderOutcome.Succeeded, null, Today, Now);
        payment.RecordCallback(ProviderOutcome.Failed, "late", Today, Now);

        payment.History.Select(h => (h.FromStatus, h.ToStatus)).ShouldBe([(PaymentStatus.Initiated, PaymentStatus.Pending), (PaymentStatus.Pending, PaymentStatus.Succeeded)]);
        payment.Status.ShouldBe(PaymentStatus.Succeeded);
        payment.DomainEvents.OfType<PaymentConfirmed>().ShouldHaveSingleItem();
    }

    [Fact]
    public void Allocation_NeverExceedsTheAmountReceived_BIL003()
    {
        var payment = New(amount: 100m);
        payment.Sent(ProviderOutcome.Succeeded, "EXT", null, Today, Now);

        payment.AllocatePayment([(Guid.CreateVersion7(), 60m)], "OLDEST_FIRST", Now).IsSuccess.ShouldBeTrue();
        payment.AllocatePayment([(Guid.CreateVersion7(), 40.01m)], "OLDEST_FIRST", Now).Error!.Code.ShouldBe(PaymentErrorCodes.OverAllocated);
        payment.Allocated.ShouldBe(60m);
    }

    [Fact]
    public void Reversal_ReversesItsAllocations()
    {
        var payment = New();
        payment.Sent(ProviderOutcome.Succeeded, "EXT", null, Today, Now);
        payment.AllocatePayment([(Guid.CreateVersion7(), 70m)], "OLDEST_FIRST", Now);

        payment.ReversePayment("Dishonoured", Today, Now).IsSuccess.ShouldBeTrue();

        payment.Allocated.ShouldBe(0m);
        payment.DomainEvents.OfType<PaymentReversed>().Single().Allocations.Sum(a => a.Amount).ShouldBe(70m);
    }

    [Fact]
    public void PayoutBatch_IsApprovedByAnotherPerson_BeforeRelease_SRS002()
    {
        var builder = Guid.CreateVersion7();
        var payments = new[] { New(Direction.Out, 10m), New(Direction.Out, 15m) };
        var batch = PayoutBatch.BuildBatch(Guid.CreateVersion7(), "CLAIM_PAYOUT", "ZAR", payments, builder, Now).Value;

        batch.TotalAmount.ShouldBe(25m);
        payments.ShouldAllBe(p => p.PayoutBatchId == batch.Id);
        batch.ReleaseBatch(builder, Now).IsFailure.ShouldBeTrue();
        batch.ApproveBatch(builder).Error!.Code.ShouldBe(ErrorCodes.MakerCheckerSamePerson);
        batch.Status.ShouldBe(BatchStatus.Built);

        batch.ApproveBatch(Guid.CreateVersion7()).IsSuccess.ShouldBeTrue();
        batch.ReleaseBatch(builder, Now).IsSuccess.ShouldBeTrue();
        batch.Settle(allPaymentsFinal: false);
        batch.Status.ShouldBe(BatchStatus.Released);
        batch.Settle(allPaymentsFinal: true);
        batch.Status.ShouldBe(BatchStatus.Settled);
    }

    [Fact]
    public void EmptyBatch_IsRefused() =>
        PayoutBatch.BuildBatch(Guid.CreateVersion7(), "REFUND", "ZAR", [], Guid.CreateVersion7(), Now).Error!.Code.ShouldBe(PaymentErrorCodes.BatchEmpty);

    private static Payment Payout(decimal amount = 100m) =>
        Payment.InitiatePayment(Guid.CreateVersion7(), "KEY-" + Guid.NewGuid(), Direction.Out, "REFUND", "EFT", "SIMULATOR", amount, "ZAR", null, "REFUND", Guid.CreateVersion7(),
            Now, Guid.CreateVersion7()).Value;

    [Fact]
    public void RejectedPayoutBatch_EndsRejected_AndReleasesItsUnsentPaymentsForTheNextBatch_D1()
    {
        var waiting = Payout();
        var other = Payout(50m);
        var batch = PayoutBatch.BuildBatch(Guid.CreateVersion7(), "REFUND", "ZAR", [waiting, other], Guid.CreateVersion7(), Now).Value;
        var moved = Payout(25m);
        moved.AssignToBatch(Guid.CreateVersion7());

        var released = batch.RejectBatch([waiting, other, moved]);

        released.IsSuccess.ShouldBeTrue();
        batch.Status.ShouldBe(BatchStatus.Rejected);
        released.Value.ShouldBe([waiting.Id, other.Id], ignoreOrder: true);
        foreach (var payment in new[] { waiting, other, moved })
        {
            payment.LeaveBatch(batch.Id);
        }

        (waiting.PayoutBatchId, other.PayoutBatchId).ShouldBe((null, null));
        moved.PayoutBatchId.ShouldNotBeNull();
        PayoutBatch.BuildBatch(Guid.CreateVersion7(), "REFUND", "ZAR", [waiting, other], Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void OnlyABatchNotYetApproved_CanBeRejected_SoNoPaymentIsPaidTwice_D1()
    {
        var builder = Guid.CreateVersion7();
        var payment = Payout();
        var batch = PayoutBatch.BuildBatch(Guid.CreateVersion7(), "REFUND", "ZAR", [payment], builder, Now).Value;
        batch.ApproveBatch(Guid.CreateVersion7()).IsSuccess.ShouldBeTrue();

        batch.RejectBatch([payment]).Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
        batch.Status.ShouldBe(BatchStatus.Approved);
        payment.PayoutBatchId.ShouldBe(batch.Id);
    }

    [Fact]
    public void ABatchApprovedByTheChecker_IsGrantedByThem_SoARedeliveredGrantChangesNothing_D5()
    {
        var checker = Guid.CreateVersion7();
        var batch = PayoutBatch.BuildBatch(Guid.CreateVersion7(), "REFUND", "ZAR", [Payout()], Guid.CreateVersion7(), Now).Value;
        batch.GrantedBy(checker).ShouldBeFalse();

        batch.ApproveBatch(checker).IsSuccess.ShouldBeTrue();
        (batch.GrantedBy(checker), batch.GrantedBy(Guid.CreateVersion7())).ShouldBe((true, false));
        batch.ApproveBatch(checker).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void ASentPayment_NeverLeavesItsBatch_D1()
    {
        var payment = Payout();
        var batch = PayoutBatch.BuildBatch(Guid.CreateVersion7(), "REFUND", "ZAR", [payment], Guid.CreateVersion7(), Now).Value;
        payment.Sent(ProviderOutcome.Pending, "EXT-1", null, Today, Now).IsSuccess.ShouldBeTrue();

        batch.RejectBatch([payment]).Value.ShouldBeEmpty();
        payment.LeaveBatch(batch.Id).ShouldBeFalse();
        payment.PayoutBatchId.ShouldBe(batch.Id);
    }
}
