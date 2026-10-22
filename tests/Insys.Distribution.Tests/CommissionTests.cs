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
        Model(CommissionStatement.Lifecycle).ShouldBe(Spec("CommissionStatement", ("Payout settled", "PayoutSettled")));
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
}
