/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Distribution.Tests
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
using Insys.Distribution.Domain;
using Insys.Distribution.Domain.Commission;
using Insys.Distribution.Domain.Intermediaries;
using Insys.Testing;

namespace Insys.Distribution.Tests;

public sealed class CommissionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 23);
    private static readonly Guid Policy = Guid.CreateVersion7();
    private static readonly StatementPayee Payee = new(Guid.CreateVersion7(), Guid.CreateVersion7(), "hash");
    private static readonly CommissionPlan Premium = new("PLAN-P", 2, CommissionBases.Premium, 12.5m, ClawbackRules.Unearned);

    private static string Spec(string aggregate, params (string From, string To)[] replacements) =>
        string.Join("|", DomainModelSpec.Transitions("commission", aggregate).Select(t => replacements.Aggregate(t, (s, r) => s.Replace(r.From, r.To, StringComparison.Ordinal)))
            .OrderBy(t => t, StringComparer.Ordinal));

    private static string Model<TState, TTrigger>(StateMachine<TState, TTrigger> machine) where TState : struct, Enum where TTrigger : struct, Enum =>
        string.Join("|", machine.Transitions.Select(t => $"{t.From}-{t.Trigger}->{t.To}").OrderBy(t => t, StringComparer.Ordinal));

    private static CommissionSource Invoice(decimal net = 1_000m, DateOnly? due = null) =>
        new("GENERAL", Guid.CreateVersion7(), Policy, "MOTOR", net, "ZAR", Today, due ?? Today.AddDays(30));

    private static CommissionEntitlement Accrued(decimal net = 1_000m, DateOnly? due = null) =>
        CommissionEntitlement.AccrueCommission(Guid.CreateVersion7(), "BRK-1", Invoice(net, due), Premium, false, Now).Value;

    [Fact]
    public void Lifecycles_MatchDomainModel()
    {
        Model(CommissionEntitlement.Lifecycle).ShouldBe(Spec("CommissionEntitlement", ("Premium received", "PremiumReceived"), ("Statement settled", "StatementSettled")));
        // Ade (24 November 2026, OQ-062 and DE-008): the nil close, the carry-forward and its ends, and the failed payout and its retry, beyond domain-model.json.
        string[] decided =
        [
            "Draft-CloseNil->ClosedNil", "Draft-CarryForward->CarriedForward", "CarriedForward-CarryForwardSettled->SettledByCarryForward",
            "CarriedForward-InvoiceClawback->Invoiced", "Approved-PayoutFailed->PaymentFailed", "PaymentFailed-RetryPayout->Approved",
        ];
        Model(CommissionStatement.Lifecycle).ShouldBe(string.Join("|", Spec("CommissionStatement", ("Payout settled", "PayoutSettled")).Split('|').Concat(decided)
            .OrderBy(t => t, StringComparer.Ordinal)));
        Model(Intermediary.Lifecycle).ShouldBe(Spec("Intermediary"));
    }

    [Fact]
    public void Accrual_RecordsThePlanVersionAndSourceTransaction_ByTheBasis_DST002_DST003()
    {
        var source = Invoice(1_234.56m);
        var premium = CommissionEntitlement.AccrueCommission(Guid.CreateVersion7(), "BRK-1", source, Premium, false, Now).Value;
        (premium.Amount, premium.BasisAmount, premium.Rate, premium.PlanCode, premium.PlanVersion, premium.SourceId).ShouldBe((154.32m, 1_234.56m, 12.5m, "PLAN-P", 2, source.InvoiceId));
        premium.DomainEvents.OfType<CommissionAccrued>().ShouldHaveSingleItem().Amount.ShouldBe(154.32m);

        var fixedPlan = Premium with { Basis = CommissionBases.Fixed, Rate = 25m };
        CommissionEntitlement.AccrueCommission(Guid.CreateVersion7(), "BRK-1", Invoice(), fixedPlan, false, Now).Value.Amount.ShouldBe(25m);

        var perPolicy = Premium with { Basis = CommissionBases.PolicyCount, Rate = 300m };
        CommissionEntitlement.AccrueCommission(Guid.CreateVersion7(), "BRK-1", Invoice(), perPolicy, true, Now).Value.Amount.ShouldBe(300m);
        CommissionEntitlement.AccrueCommission(Guid.CreateVersion7(), "BRK-1", Invoice(), perPolicy, false, Now).Error!.Code.ShouldBe(DistributionErrorCodes.NothingToAccrue);
    }

    [Fact]
    public void Clawback_AlwaysReferencesItsEntitlement_AndAPaidEntitlementIsNotClawedBack()
    {
        var accrued = Accrued();
        var clawback = accrued.ClawBack("POLICY_CANCELLED", Today, Now).Value;
        (accrued.Status, clawback.EntitlementId, clawback.Amount).ShouldBe((EntitlementStatus.ClawedBack, accrued.Id, 125m));
        accrued.ClawBack("AGAIN", Today, Now).Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition);

        var statement = CommissionStatement.Open(Guid.CreateVersion7(), "BRK-1", "2026-10", "ZAR");
        var paid = Accrued();
        paid.MarkPayable().IsSuccess.ShouldBeTrue();
        statement.PrepareStatement([paid], [], [], Payee, Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        statement.ApproveStatement(true, Guid.CreateVersion7(), Guid.CreateVersion7(), statement.SubjectHash, Now).IsSuccess.ShouldBeTrue();
        statement.PayoutSettled(Guid.CreateVersion7(), Today, [paid]).IsSuccess.ShouldBeTrue();
        paid.Status.ShouldBe(EntitlementStatus.Paid);
        paid.ClawBack("POLICY_LAPSED", Today, Now).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Statement_IsApprovedByASecondPersonBeforePayment_AndRecoversClawbacksOnTheNextOne_DST004()
    {
        var first = Accrued();
        var second = Accrued(2_000m);
        first.MarkPayable();
        second.MarkPayable();
        var october = CommissionStatement.Open(Guid.CreateVersion7(), "BRK-1", "2026-10", "ZAR");
        october.PrepareStatement([first, second], [], [], Payee, Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        (october.GrossAmount, october.TaxAmount, october.NetAmount, october.ApprovalPending).ShouldBe((375m, 0m, 375m, true));
        october.DomainEvents.OfType<StatementSubmitted>().ShouldHaveSingleItem().SubjectHash.ShouldBe(october.SubjectHash);

        // A decision for another subject does not apply; a rejection returns it to draft, to be prepared again.
        october.ApproveStatement(true, Guid.CreateVersion7(), Guid.CreateVersion7(), "other", Now).Error!.Code.ShouldBe(DistributionErrorCodes.StatementChanged);
        october.ApproveStatement(false, Guid.CreateVersion7(), Guid.CreateVersion7(), october.SubjectHash, Now).IsSuccess.ShouldBeTrue();
        (october.Status, october.ApprovalPending).ShouldBe((StatementStatus.Draft, false));

        // Prepared again; approved; then the second is clawed back before the payout: it is paid with the statement and recovered on the next one.
        october.PrepareStatement([], [], [first, second], Payee, Guid.CreateVersion7(), Now).IsFailure.ShouldBeTrue();
        october.PrepareStatement([first, second], [], [first, second], Payee, Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        october.ApproveStatement(true, Guid.CreateVersion7(), Guid.CreateVersion7(), october.SubjectHash, Now).IsSuccess.ShouldBeTrue();
        october.DomainEvents.OfType<CommissionStatementApproved>().ShouldHaveSingleItem().NetAmount.ShouldBe(375m);
        second.ClawBack("POLICY_CANCELLED", Today, Now).IsSuccess.ShouldBeTrue();
        october.PayoutSettled(Guid.CreateVersion7(), Today, [first, second]).IsSuccess.ShouldBeTrue();
        (first.Status, second.Status, second.NeedsRecovery).ShouldBe((EntitlementStatus.Paid, EntitlementStatus.ClawedBack, true));

        // A draft drops an entitlement clawed back before approval: nothing was paid, nothing to recover.
        var draft = CommissionStatement.Open(Guid.CreateVersion7(), "BRK-2", "2026-10", "ZAR");
        var other = CommissionEntitlement.AccrueCommission(Guid.CreateVersion7(), "BRK-2", Invoice(), Premium, false, Now).Value;
        other.MarkPayable();
        draft.PrepareStatement([other], [], [], Payee, Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        draft.ApproveStatement(false, Guid.CreateVersion7(), Guid.CreateVersion7(), draft.SubjectHash, Now);
        other.ClawBack("POLICY_CANCELLED", Today, Now).IsSuccess.ShouldBeTrue();
        draft.PrepareStatement([], [], [other], Payee, Guid.CreateVersion7(), Now).Error!.Code.ShouldBe(DistributionErrorCodes.StatementEmpty);
        (other.StatementId, other.NeedsRecovery).ShouldBe((null, false));

        var third = Accrued(4_000m);
        third.MarkPayable();
        var november = CommissionStatement.Open(Guid.CreateVersion7(), "BRK-1", "2026-11", "ZAR");
        november.PrepareStatement([third], [second], [], Payee, Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        (november.NetAmount, second.Clawback!.StatementId).ShouldBe((500m - 250m, november.Id));
    }

    [Fact]
    public void Statement_WithNothingDue_IsRefused()
    {
        CommissionStatement.Open(Guid.CreateVersion7(), "BRK-1", "2026-10", "ZAR").PrepareStatement([], [], [], Payee, Guid.CreateVersion7(), Now).Error!.Code
            .ShouldBe(DistributionErrorCodes.StatementEmpty);
        CommissionStatement.IsPeriod("2026-13").ShouldBeFalse();
    }

    [Fact]
    public void IntermediaryChanges_FollowItsLifecycle_DST001()
    {
        Intermediary.CheckChange(null, "ACTIVE").IsSuccess.ShouldBeTrue();
        Intermediary.CheckChange(null, "SUSPENDED").IsFailure.ShouldBeTrue();
        Intermediary.CheckChange("ACTIVE", "SUSPENDED").IsSuccess.ShouldBeTrue();
        Intermediary.CheckChange("SUSPENDED", "ACTIVE").IsSuccess.ShouldBeTrue();
        Intermediary.CheckChange("SUSPENDED", "TERMINATED").IsSuccess.ShouldBeTrue();
        Intermediary.CheckChange("ACTIVE", "ACTIVE").IsSuccess.ShouldBeTrue();
        Intermediary.CheckChange("TERMINATED", "ACTIVE").Error!.Code.ShouldBe(DistributionErrorCodes.IntermediaryTransition);
        Intermediary.CheckChange("TERMINATED", "TERMINATED").IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void StatementEnding_ReturnsItToDraft_AndReleasesItsEntitlementsAndRecoveriesForTheNextStatement_DST004()
    {
        // Clawed back after its statement was approved, so paid with it: its recovery goes on the next statement.
        var paid = Accrued();
        paid.MarkPayable();
        var september = CommissionStatement.Open(Guid.CreateVersion7(), "BRK-1", "2026-09", "ZAR");
        september.PrepareStatement([paid], [], [], Payee, Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        september.ApproveStatement(true, Guid.CreateVersion7(), Guid.CreateVersion7(), september.SubjectHash, Now);
        paid.ClawBack("POLICY_CANCELLED", Today, Now).IsSuccess.ShouldBeTrue();
        september.PayoutSettled(Guid.CreateVersion7(), Today, [paid]);
        paid.NeedsRecovery.ShouldBeTrue();

        var payable = Accrued(2_000m);
        payable.MarkPayable();
        var october = CommissionStatement.Open(Guid.CreateVersion7(), "BRK-1", "2026-10", "ZAR");
        october.PrepareStatement([payable], [paid], [], Payee, Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        (payable.StatementId, paid.Clawback!.StatementId).ShouldBe((october.Id, october.Id));

        // Expired (no checker): back to draft; nothing held by it any more.
        var approvalId = Guid.CreateVersion7();
        october.ApproveStatement(false, approvalId, Guid.Empty, october.SubjectHash, Now, [payable, paid]).IsSuccess.ShouldBeTrue();
        (october.Status, october.ApprovalPending, october.EndedBy(approvalId)).ShouldBe((StatementStatus.Draft, false, true));
        (payable.StatementId, paid.Clawback!.StatementId, paid.NeedsRecovery).ShouldBe(((Guid?)null, (Guid?)null, true));

        // Applied again (a redelivery), it changes nothing; the next prepared statement takes them.
        october.ApproveStatement(false, approvalId, Guid.Empty, october.SubjectHash, Now, [payable, paid]).IsSuccess.ShouldBeTrue();
        var november = CommissionStatement.Open(Guid.CreateVersion7(), "BRK-1", "2026-11", "ZAR");
        november.PrepareStatement([payable], [paid], [], Payee, Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        (payable.StatementId, paid.Clawback!.StatementId).ShouldBe((november.Id, november.Id));
    }

    [Fact]
    public void AStatementApprovedByAnApproval_IsGrantedByIt_AndAChangedOneIsNot_D5()
    {
        var payable = Accrued();
        payable.MarkPayable();
        var statement = CommissionStatement.Open(Guid.CreateVersion7(), "BRK-1", "2026-10", "ZAR");
        statement.PrepareStatement([payable], [], [], Payee, Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        var approvalId = Guid.CreateVersion7();

        statement.ApproveStatement(true, approvalId, Guid.CreateVersion7(), "the statement before a change", Now).Error!.Code
            .ShouldBe(DistributionErrorCodes.StatementChanged);
        statement.GrantedBy(approvalId).ShouldBeFalse();
        statement.ApproveStatement(true, approvalId, Guid.CreateVersion7(), statement.SubjectHash, Now).IsSuccess.ShouldBeTrue();
        (statement.GrantedBy(approvalId), statement.GrantedBy(Guid.CreateVersion7())).ShouldBe((true, false));
    }

    [Fact]
    public void StatementEnding_ForAnotherSubjectOrAStatementNotAwaitingApproval_IsRefused_D5()
    {
        var payable = Accrued();
        payable.MarkPayable();
        var statement = CommissionStatement.Open(Guid.CreateVersion7(), "BRK-1", "2026-10", "ZAR");
        statement.ApproveStatement(false, Guid.CreateVersion7(), Guid.Empty, null, Now).Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
        statement.PrepareStatement([payable], [], [], Payee, Guid.CreateVersion7(), Now);

        statement.ApproveStatement(false, Guid.CreateVersion7(), Guid.CreateVersion7(), "other", Now, [payable]).Error!.Code.ShouldBe(DistributionErrorCodes.StatementChanged);
        (statement.ApprovalPending, payable.StatementId).ShouldBe((true, (Guid?)statement.Id));
    }

    /// <summary>B-001 (OQ-135): the outgoing broker's commission not yet on a statement is cut to its share before the change; nothing else is.</summary>
    [Fact]
    public void WhatABrokerOwes_ComesOffAStatement_AsFarAsItHasCommission_AndTheRestStaysOwed_B009()
    {
        var paid = Accrued();
        var recovery = CommissionRecovery.Raised(Guid.CreateVersion7(), paid, 500m, "BROKER_CHANGED", new DateOnly(2026, 10, 16), Now);
        recovery.DomainEvents.OfType<CommissionClawedBack>().Single().Amount.ShouldBe(500m);
        var payable = Accrued();
        payable.MarkPayable().IsSuccess.ShouldBeTrue();
        var statement = CommissionStatement.Open(Guid.CreateVersion7(), "BRK-1", "2026-10", "ZAR");
        statement.PrepareStatement([payable], [], [], Payee, Guid.CreateVersion7(), Now, [recovery]).IsSuccess.ShouldBeTrue();
        statement.NetAmount.ShouldBe(0m, "it takes all there is, and goes no lower");
        (recovery.OnStatementAmount, recovery.Owed).ShouldBe((payable.Amount, 500m - payable.Amount));

        recovery.Release();
        (recovery.OnStatementAmount, recovery.Owed, recovery.StatementId).ShouldBe((0m, 500m, (Guid?)null), "a rejected statement gives it back");

        var again = CommissionStatement.Open(Guid.CreateVersion7(), "BRK-1", "2026-11", "ZAR");
        var next = Accrued();
        next.MarkPayable().IsSuccess.ShouldBeTrue();
        again.PrepareStatement([next], [], [], Payee, Guid.CreateVersion7(), Now, [recovery]).IsSuccess.ShouldBeTrue();
        recovery.Settle();
        (recovery.RecoveredAmount, recovery.Owed).ShouldBe((next.Amount, 500m - next.Amount));
    }

    [Fact]
    public void ABrokerOfRecordChange_CutsTheOutgoingCommissionToItsShare_OnlyBeforeAStatement_B001()
    {
        var entitlement = CommissionEntitlement.AccrueCommission(Guid.CreateVersion7(), "BRK-1", Invoice(1_000m, new DateOnly(2026, 10, 1)), Premium, false, Now).Value;
        entitlement.Share(1m, "BROKER_OF_RECORD_CHANGED", new DateOnly(2026, 10, 16), Now).IsFailure.ShouldBeTrue("a share is a part");
        entitlement.Share(0.5m, "BROKER_OF_RECORD_CHANGED", new DateOnly(2026, 10, 16), Now).IsSuccess.ShouldBeTrue();
        (entitlement.Amount, entitlement.BasisAmount, entitlement.Status).ShouldBe((62.5m, 500m, EntitlementStatus.Accrued));
        entitlement.DomainEvents.OfType<CommissionShared>().Single().ShouldSatisfyAllConditions(e => e.AmountBefore.ShouldBe(125m), e => e.Amount.ShouldBe(62.5m));
    }
    private static CommissionEntitlement Payable(decimal net = 1_000m)
    {
        var entitlement = Accrued(net);
        entitlement.MarkPayable().IsSuccess.ShouldBeTrue();
        return entitlement;
    }

    /// <summary>A statement approved with the clawbacks it recovers (each the 125 of a paid entitlement) against its commission.</summary>
    private static (CommissionStatement Statement, CommissionEntitlement[] Lines) Approved(string period, decimal commissionNet, int clawbacks,
        CommissionStatement[]? carried = null, DateOnly? on = null)
    {
        var recover = new List<CommissionEntitlement>();
        for (var i = 0; i < clawbacks; i++)
        {
            var earlier = Payable();
            var previous = CommissionStatement.Open(Guid.CreateVersion7(), "BRK-1", "2025-01", "ZAR");
            previous.PrepareStatement([earlier], [], [], Payee, Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
            previous.ApproveStatement(true, Guid.CreateVersion7(), Guid.CreateVersion7(), previous.SubjectHash, Now).IsSuccess.ShouldBeTrue();
            earlier.ClawBack("POLICY_CANCELLED", Today, Now).IsSuccess.ShouldBeTrue(); // on an approved statement, not yet paid: recovered on the next
            recover.Add(earlier);
        }

        CommissionEntitlement[] payable = commissionNet > 0 ? [Payable(commissionNet)] : [];
        var statement = CommissionStatement.Open(Guid.CreateVersion7(), "BRK-1", period, "ZAR");
        statement.PrepareStatement(payable, recover, [], Payee, Guid.CreateVersion7(), Now, null, carried).IsSuccess.ShouldBeTrue();
        statement.ApproveStatement(true, Guid.CreateVersion7(), Guid.CreateVersion7(), statement.SubjectHash, Now, [.. payable, .. recover], on ?? Today).IsSuccess
            .ShouldBeTrue();
        return (statement, [.. payable, .. recover]);
    }

    [Fact]
    public void AStatementApprovedWithANetOfZero_ClosesNil_WithNoPayout_AndItsEntitlementsSettled_OQ062()
    {
        var (statement, lines) = Approved("2026-10", 1_000m, 1); // 125 commission less one 125 clawback
        statement.NetAmount.ShouldBe(0m);
        statement.Status.ShouldBe(StatementStatus.ClosedNil);
        statement.DomainEvents.OfType<CommissionStatementApproved>().ShouldBeEmpty("nothing is paid");
        lines[0].Status.ShouldBe(EntitlementStatus.Paid, "settled by the nil close, never counted again");
        statement.CarriedAmount.ShouldBe(0m);
    }

    [Fact]
    public void AStatementApprovedWithANegativeNet_IsCarriedForward_OnTheBusinessDate_AndItsLinesAreSettledByIt_OQ062()
    {
        var carriedOn = new DateOnly(2026, 10, 31);
        var (statement, lines) = Approved("2026-10", 1_000m, 3, on: carriedOn); // 125 - 375
        (statement.Status, statement.NetAmount, statement.CarriedAmount, statement.CarriedOn, statement.CarriedOwed)
            .ShouldBe((StatementStatus.CarriedForward, -250m, 250m, (DateOnly?)carriedOn, 250m));
        statement.DomainEvents.OfType<CommissionStatementApproved>().ShouldBeEmpty();
        lines[0].Status.ShouldBe(EntitlementStatus.Paid);
        lines.Skip(1).ShouldAllBe(e => e.Clawback!.StatementId == statement.Id && !e.NeedsRecovery, "its clawbacks are recovered by it, never on a later statement");
    }

    [Fact]
    public void ACarriedAmount_IsTheOpeningLineOfTheNextStatements_AsFarAsTheirCommissionCovers_ThenSettledByCarryForward_OQ062()
    {
        var (october, _) = Approved("2026-10", 0m, 2); // carries 250

        // November has 125: it takes 125 as its opening line and nets to nil; 125 stays carried.
        var november = CommissionStatement.Open(Guid.CreateVersion7(), "BRK-1", "2026-11", "ZAR");
        var novemberLine = Payable();
        november.PrepareStatement([novemberLine], [], [], Payee, Guid.CreateVersion7(), Now, null, [october]).IsSuccess.ShouldBeTrue();
        (november.OpeningAmount, november.NetAmount, october.CarryOnStatementId, october.CarriedOwed).ShouldBe((125m, 0m, (Guid?)november.Id, 125m));

        // Rejected, it gives the amount back; prepared again it takes it again.
        november.ApproveStatement(false, Guid.CreateVersion7(), Guid.Empty, november.SubjectHash, Now, [novemberLine]);
        october.ReleaseCarry();
        october.CarriedOwed.ShouldBe(250m);
        november.PrepareStatement([novemberLine], [], [novemberLine], Payee, Guid.CreateVersion7(), Now, null, [october]).IsSuccess.ShouldBeTrue();
        november.ApproveStatement(true, Guid.CreateVersion7(), Guid.CreateVersion7(), november.SubjectHash, Now, [novemberLine], Today).IsSuccess.ShouldBeTrue();
        november.Status.ShouldBe(StatementStatus.ClosedNil);
        october.SettleCarry();
        (october.Status, october.CarrySettledAmount, october.CarriedOwed).ShouldBe((StatementStatus.CarriedForward, 125m, 125m));

        // December has 1,250: it takes the remaining 125 and pays 1,125; paid, October is settled by the carry-forward.
        var december = CommissionStatement.Open(Guid.CreateVersion7(), "BRK-1", "2026-12", "ZAR");
        var decemberLine = Payable(10_000m);
        december.PrepareStatement([decemberLine], [], [], Payee, Guid.CreateVersion7(), Now, null, [october]).IsSuccess.ShouldBeTrue();
        (december.OpeningAmount, december.NetAmount).ShouldBe((125m, 1_125m));
        december.ApproveStatement(true, Guid.CreateVersion7(), Guid.CreateVersion7(), december.SubjectHash, Now, [decemberLine], Today).IsSuccess.ShouldBeTrue();
        december.DomainEvents.OfType<CommissionStatementApproved>().ShouldHaveSingleItem().NetAmount.ShouldBe(1_125m);
        december.PayoutSettled(Guid.CreateVersion7(), Today, [decemberLine]).IsSuccess.ShouldBeTrue();
        october.SettleCarry();
        (october.Status, october.CarrySettledAmount, october.CarriedOwed).ShouldBe((StatementStatus.SettledByCarryForward, 250m, 0m));
    }

    [Fact]
    public void AClawbackInvoice_NeedsTheTypedReference_AndTerminationOrTheTenantsDays_ThenASecondPerson_OQ062()
    {
        var carriedOn = new DateOnly(2026, 8, 1);
        var (statement, _) = Approved("2026-07", 0m, 2, on: carriedOn); // carries 250
        statement.Reference.ShouldBe("BRK-1/2026-07");
        var requester = Guid.CreateVersion7();

        statement.RequestClawbackInvoice("BRK-1/2026-08", "Broker left", false, 90, carriedOn.AddDays(120), requester, Now).Error!.Code
            .ShouldBe(ErrorCodes.TypedReferenceMismatch);
        statement.RequestClawbackInvoice("BRK-1/2026-07", "Broker left", false, 90, carriedOn.AddDays(89), requester, Now).Error!.Code
            .ShouldBe(DistributionErrorCodes.ClawbackInvoiceNotDue);
        statement.PendingAction.ShouldBeNull("a refusal changes nothing");
        statement.RequestClawbackInvoice("BRK-1/2026-07", "Broker left", true, 90, carriedOn.AddDays(1), requester, Now).IsSuccess.ShouldBeTrue("terminated: at once");
        var asked = statement.DomainEvents.OfType<StatementActionRequested>().ShouldHaveSingleItem();
        (asked.ApprovalType, asked.Amount, asked.RequestedBy, asked.SubjectHash).ShouldBe((StatementActions.ClawbackInvoice, 250m, requester, statement.ActionSubjectHash));
        statement.RequestClawbackInvoice("BRK-1/2026-07", "Again", true, 90, carriedOn.AddDays(1), requester, Now).Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition);

        // Rejected (or expired, withdrawn): it stays carried; redelivered, nothing changes.
        var rejection = Guid.CreateVersion7();
        statement.DecideAction(false, StatementActions.ClawbackInvoice, rejection, statement.ActionSubjectHash, Today, Now).IsSuccess.ShouldBeTrue();
        (statement.Status, statement.PendingAction).ShouldBe((StatementStatus.CarriedForward, (string?)null));
        statement.DecideAction(false, StatementActions.ClawbackInvoice, rejection, null, Today, Now).IsSuccess.ShouldBeTrue();

        // After 90 days: asked again and granted, the carried amount is closed as invoiced and Billing is asked to invoice it.
        statement.RequestClawbackInvoice("BRK-1/2026-07", "Owed for over 90 days", false, 90, carriedOn.AddDays(90), requester, Now).IsSuccess.ShouldBeTrue();
        statement.DecideAction(true, StatementActions.ClawbackInvoice, Guid.CreateVersion7(), "another subject", Today, Now).Error!.Code
            .ShouldBe(DistributionErrorCodes.StatementChanged);
        statement.DecideAction(true, StatementActions.ClawbackInvoice, Guid.CreateVersion7(), statement.ActionSubjectHash, Today, Now).IsSuccess.ShouldBeTrue();
        (statement.Status, statement.InvoicedOn, statement.InvoicedAmount, statement.CarriedOwed).ShouldBe((StatementStatus.Invoiced, (DateOnly?)Today, (decimal?)250m, 0m));
        var invoice = statement.DomainEvents.OfType<ClawbackInvoiceRequested>().ShouldHaveSingleItem();
        (invoice.PayerPartyId, invoice.Amount, invoice.Reference, invoice.DueOn).ShouldBe((Payee.PartyId, 250m, "BRK-1/2026-07", Today));
    }

    [Fact]
    public void ACarriedAmountPartlyOnALaterStatement_IsNotInvoiced_AndOneAwaitingItsInvoice_IsNotTaken_OQ062()
    {
        var (october, _) = Approved("2026-10", 0m, 2);
        var november = CommissionStatement.Open(Guid.CreateVersion7(), "BRK-1", "2026-11", "ZAR");
        november.PrepareStatement([Payable()], [], [], Payee, Guid.CreateVersion7(), Now, null, [october]).IsSuccess.ShouldBeTrue();
        october.RequestClawbackInvoice(october.Reference, "Owed", true, 90, Today, Guid.CreateVersion7(), Now).Error!.Code.ShouldBe(DistributionErrorCodes.CarryOnStatement);

        october.ReleaseCarry();
        october.RequestClawbackInvoice(october.Reference, "Owed", true, 90, Today, Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        var december = CommissionStatement.Open(Guid.CreateVersion7(), "BRK-1", "2026-12", "ZAR");
        december.PrepareStatement([Payable()], [], [], Payee, Guid.CreateVersion7(), Now, null, [october]).IsSuccess.ShouldBeTrue();
        (december.OpeningAmount, december.NetAmount, october.CarryOnStatementId).ShouldBe((0m, 125m, (Guid?)null));
    }

    [Fact]
    public void AFailedPayout_IsPaymentFailed_AndARetryNeedsTheTypedPaymentRefAndASecondPerson_ThenTheNextAttempt_DE008()
    {
        var line = Payable();
        var statement = CommissionStatement.Open(Guid.CreateVersion7(), "BRK-1", "2026-10", "ZAR");
        statement.PrepareStatement([line], [], [], Payee, Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        statement.ApproveStatement(true, Guid.CreateVersion7(), Guid.CreateVersion7(), statement.SubjectHash, Now, [line], Today).IsSuccess.ShouldBeTrue();
        statement.RequestPayoutRetry("PAY-1", Guid.CreateVersion7(), Now).Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition, "nothing failed");

        statement.PayoutFailed("PAY-0001").IsSuccess.ShouldBeTrue();
        statement.PayoutFailed("PAY-0001").IsSuccess.ShouldBeTrue("a redelivery");
        (statement.Status, statement.FailedPaymentRef, line.Status).ShouldBe((StatementStatus.PaymentFailed, "PAY-0001", EntitlementStatus.Payable));

        var requester = Guid.CreateVersion7();
        statement.RequestPayoutRetry("PAY-0002", requester, Now).Error!.Code.ShouldBe(ErrorCodes.TypedReferenceMismatch);
        statement.PendingAction.ShouldBeNull();
        statement.RequestPayoutRetry("PAY-0001", requester, Now).IsSuccess.ShouldBeTrue();
        var asked = statement.DomainEvents.OfType<StatementActionRequested>().ShouldHaveSingleItem();
        (asked.ApprovalType, asked.Amount, asked.Currency).ShouldBe((StatementActions.PayoutRetry, 125m, "ZAR"));

        // Rejected: it stays failed. Granted: approved again for attempt 2, asking Payments for the payout.
        statement.DecideAction(false, StatementActions.PayoutRetry, Guid.CreateVersion7(), statement.ActionSubjectHash, Today, Now).IsSuccess.ShouldBeTrue();
        (statement.Status, statement.PendingAction).ShouldBe((StatementStatus.PaymentFailed, (string?)null));
        statement.RequestPayoutRetry("PAY-0001", requester, Now).IsSuccess.ShouldBeTrue();
        var granted = Guid.CreateVersion7();
        statement.DecideAction(true, StatementActions.ClawbackInvoice, granted, null, Today, Now).Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
        statement.DecideAction(true, StatementActions.PayoutRetry, granted, statement.ActionSubjectHash, Today, Now).IsSuccess.ShouldBeTrue();
        statement.DecideAction(true, StatementActions.PayoutRetry, granted, null, Today, Now).IsSuccess.ShouldBeTrue("a redelivery");
        (statement.Status, statement.PayoutAttempt).ShouldBe((StatementStatus.Approved, 2));
        var retried = statement.DomainEvents.OfType<StatementPayoutRetried>().ShouldHaveSingleItem();
        (retried.Attempt, retried.NetAmount, retried.PayeePartyId, retried.BankAccountId).ShouldBe((2, 125m, Payee.PartyId, Payee.BankAccountId));

        statement.PayoutSettled(Guid.CreateVersion7(), Today, [line]).IsSuccess.ShouldBeTrue();
        (statement.Status, line.Status).ShouldBe((StatementStatus.Paid, EntitlementStatus.Paid));
    }

    [Fact]
    public void TheStatementActions_AreTheSharedApprovalTypes_DE008()
    {
        StatementActions.PayoutRetry.ShouldBe(Insys.Payments.Contracts.PaymentApprovals.PayoutRetryApprovalType);
        StatementActions.ClawbackInvoice.ShouldBe(Insys.Distribution.Contracts.DistributionApprovals.ClawbackInvoiceApprovalType);
    }
}
