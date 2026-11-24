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
using Insys.BuildingBlocks.Domain;
using Insys.Health.Domain;
using Insys.Health.Domain.Payments;
using Shouldly;

namespace Insys.Health.Tests;

/// <summary>
/// DE-008 (Ade, 24 November 2026): a failed remittance is FAILED with its payment's reference, keeps the run RELEASED, and is paid again only with that
/// reference typed and a second person's approval (PAYOUT_RETRY, SRS-002), as a new payout attempt.
/// </summary>
public sealed class RemittanceReissueTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 24);
    private static readonly Guid Maker = Guid.CreateVersion7();

    private static ProviderPaymentRun Released(out RemittanceAdvice failing, out RemittanceAdvice other)
    {
        var run = ProviderPaymentRun.OpenRun(Guid.CreateVersion7(), "PRV-WEEKLY", "ZAR", Now);
        run.CutOffRun(Now);
        run.CalculateRun([new RemittanceRequest("PR-1", Guid.CreateVersion7(), Guid.CreateVersion7(), 900m, 1), new RemittanceRequest("PR-2", Guid.CreateVersion7(),
            Guid.CreateVersion7(), 450.50m, 2), new RemittanceRequest("PR-3", Guid.CreateVersion7(), Guid.CreateVersion7(), 0m, 0, 100m, 100m)], Maker, Now).IsSuccess.ShouldBeTrue();
        run.DecideApproval(true, Guid.CreateVersion7(), Guid.CreateVersion7(), run.SubjectHash).IsSuccess.ShouldBeTrue();
        run.ReleaseRun(Today, "TPL-RA", Now).IsSuccess.ShouldBeTrue();
        (failing, other) = (run.Remittances[0], run.Remittances[1]);
        run.Remittances[2].PaymentStatus.ShouldBe(RemittancePaymentStatuses.None, "an advice with no payment (gap 2)");
        return run;
    }

    [Fact]
    public void AFailedRemittance_IsFailedWithItsReference_AndKeepsTheRunReleased_UntilItIsPaid_DE008_HLT009()
    {
        var run = Released(out var failing, out var other);
        run.RecordPaymentFailure(failing.Id, Guid.CreateVersion7(), "PAY-0001", "Account closed").ShouldBeTrue();
        run.RecordPayment(other.Id, Guid.CreateVersion7(), Today);

        (failing.PaymentStatus, failing.FailedPaymentRef, failing.FailureReason).ShouldBe((RemittancePaymentStatuses.Failed, "PAY-0001", "Account closed"));
        run.Status.ShouldBe(RunStatus.Released, "a failed remittance holds the run");
        run.RecordPayment(failing.Id, Guid.CreateVersion7(), Today);
        run.Status.ShouldBe(RunStatus.Reconciled);
    }

    [Fact]
    public void AFailureRedelivered_OrAfterThePayment_ChangesNothing_DE008()
    {
        var run = Released(out var failing, out _);
        var payment = Guid.CreateVersion7();
        run.RecordPaymentFailure(failing.Id, payment, "PAY-0001", "Rejected").ShouldBeTrue();
        run.RecordPaymentFailure(failing.Id, payment, "PAY-0001", "Rejected").ShouldBeFalse();
        run.RecordPayment(failing.Id, Guid.CreateVersion7(), Today);
        run.RecordPaymentFailure(failing.Id, Guid.CreateVersion7(), "PAY-0002", "Late").ShouldBeFalse();
        failing.PaymentStatus.ShouldBe(RemittancePaymentStatuses.Paid);
    }

    [Fact]
    public void AReissue_NeedsTheFailedReferenceTyped_AndChangesNothingOtherwise_DE008()
    {
        var run = Released(out var failing, out var other);
        run.RecordPaymentFailure(failing.Id, Guid.CreateVersion7(), "PAY-0001", "Account closed");

        run.RequestRemittanceReissue(failing.Id, "PAY-0009", "Provider gave new details", null, Maker, Now).Error!.Code.ShouldBe(ErrorCodes.TypedReferenceMismatch);
        failing.ReissueRequestedBy.ShouldBeNull();
        run.DomainEvents.OfType<RemittanceReissueRequested>().ShouldBeEmpty();
        run.RequestRemittanceReissue(other.Id, "PAY-0001", "Not failed", null, Maker, Now).Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
        run.RequestRemittanceReissue(failing.Id, "PAY-0001", " ", null, Maker, Now).Error!.Code.ShouldBe(HealthErrorCodes.ReasonRequired);

        run.RequestRemittanceReissue(failing.Id, " PAY-0001 ", "Provider gave new details", null, Maker, Now).IsSuccess.ShouldBeTrue();
        var requested = run.DomainEvents.OfType<RemittanceReissueRequested>().Single();
        (requested.RemittanceId, requested.Amount, requested.Currency, requested.RequestedBy).ShouldBe((failing.Id, 900m, "ZAR", Maker));
        run.RequestRemittanceReissue(failing.Id, "PAY-0001", "Again", null, Maker, Now).Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition, "one already waits");
    }

    [Fact]
    public void AGrantedReissue_RequestsTheNextAttempt_ToTheNamedAccount_AndARejectedOneStaysFailed_DE008_SRS002()
    {
        var run = Released(out var failing, out _);
        run.RecordPaymentFailure(failing.Id, Guid.CreateVersion7(), "PAY-0001", "Account closed");
        var account = Guid.CreateVersion7();
        run.RequestRemittanceReissue(failing.Id, "PAY-0001", "New account", account, Maker, Now);
        var hash = run.DomainEvents.OfType<RemittanceReissueRequested>().Single().SubjectHash;

        var rejection = Guid.CreateVersion7();
        run.DecideRemittanceReissue(failing.Id, false, rejection, hash, Now).IsSuccess.ShouldBeTrue();
        (failing.PaymentStatus, failing.ReissueRequestedBy, failing.Attempt).ShouldBe((RemittancePaymentStatuses.Failed, (Guid?)null, 1));
        run.ReissueDecidedBy(failing.Id, rejection).ShouldBeTrue();

        run.RequestRemittanceReissue(failing.Id, "PAY-0001", "New account", account, Maker, Now);
        hash = run.DomainEvents.OfType<RemittanceReissueRequested>().Last().SubjectHash;
        run.DecideRemittanceReissue(failing.Id, true, Guid.CreateVersion7(), "another subject", Now).Error!.Code.ShouldBe(HealthErrorCodes.ApprovalSubjectChanged);
        run.DecideRemittanceReissue(failing.Id, true, Guid.CreateVersion7(), hash, Now).IsSuccess.ShouldBeTrue();

        var payout = run.DomainEvents.OfType<RemittancePayoutReissued>().Single();
        (payout.Attempt, payout.Amount, payout.BankAccountId, payout.ProviderPartyId).ShouldBe((2, 900m, account, failing.ProviderPartyId));
        (failing.PaymentStatus, failing.PayoutBankAccountId, failing.BankAccountId == account).ShouldBe((RemittancePaymentStatuses.Pending, (Guid?)account, false));
        run.Status.ShouldBe(RunStatus.Released);
    }
}
