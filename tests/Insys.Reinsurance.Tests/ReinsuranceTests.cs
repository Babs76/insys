using Insys.BuildingBlocks.Domain;
using Insys.Reinsurance.Domain;
using Insys.Testing;

namespace Insys.Reinsurance.Tests;

/// <summary>Treaty arithmetic per OQ-063 (engineering cases, pending business sign-off).</summary>
public sealed class ReinsuranceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Start = new(2026, 11, 1);
    private static readonly TreatyTerms QuotaShare = new("QS-2026", 3, TreatyTypes.QuotaShare, "30", 5_000_000m, new(2026, 1, 1), new(2026, 12, 31), "RE-1");
    private static readonly TreatyTerms Surplus = new("SUR-2026", 1, TreatyTypes.Surplus, "1000000", 4_000_000m, new(2026, 1, 1), new(2026, 12, 31), null);
    private static readonly TreatyTerms Excess = new("XL-2026", 1, TreatyTypes.ExcessOfLoss, "250000", 1_000_000m, new(2026, 1, 1), new(2026, 12, 31), null);
    private static readonly CessionRule Rule = new("CR-MOTOR", 2, "QS-2026", ["MOTOR"], AllocationBases.SumInsured);

    private static PolicyRisk Risk(decimal sumInsured, decimal premium = 12_000m) =>
        new("GENERAL", Guid.CreateVersion7(), "GP-1", "MOTOR", sumInsured, premium, "ZAR", Start, Start.AddYears(1).AddDays(-1));

    private static string Spec(string aggregate) =>
        string.Join("|", DomainModelSpec.Transitions("reinsurance", aggregate).OrderBy(t => t, StringComparer.Ordinal));

    private static string Model<TState, TTrigger>(StateMachine<TState, TTrigger> machine) where TState : struct, Enum where TTrigger : struct, Enum =>
        string.Join("|", machine.Transitions.Select(t => $"{t.From}-{t.Trigger}->{t.To}").OrderBy(t => t, StringComparer.Ordinal));

    [Fact]
    public void Lifecycles_MatchDomainModel()
    {
        Model(ReinsuranceRecovery.Lifecycle).ShouldBe(Spec("ReinsuranceRecovery"));
        Model(Bordereau.Lifecycle).ShouldBe(Spec("Bordereau"));
    }

    [Fact]
    public void QuotaShare_CedesTheRestOfTheRetention_UpToCapacity_AndRecordsWhatItWasCalculatedFrom_REI001_REI002()
    {
        var risk = Risk(2_000_000m);
        var cession = Cession.CalculateCession(Guid.CreateVersion7(), risk, QuotaShare, Rule, null, Now).Value;
        (cession.CededShare, cession.CededSumInsured, cession.CededPremium).ShouldBe((0.7m, 1_400_000m, 8_400m));
        (cession.TreatyCode, cession.TreatyVersion, cession.RuleCode, cession.RuleVersion, cession.SourceId, cession.GrossSumInsured, cession.GrossPremium)
            .ShouldBe(("QS-2026", (int?)3, "CR-MOTOR", 2, risk.PolicyRef, 2_000_000m, 12_000m));

        // Capacity caps it.
        Cession.CalculateCession(Guid.CreateVersion7(), Risk(10_000_000m), QuotaShare, Rule, null, Now).Value.CededSumInsured.ShouldBe(5_000_000m);
    }

    [Fact]
    public void Surplus_CedesWhatExceedsTheRetention_AndNothingBelowIt()
    {
        var cession = Cession.CalculateCession(Guid.CreateVersion7(), Risk(1_600_000m), Surplus, Rule with { TreatyCode = "SUR-2026" }, null, Now).Value;
        (cession.CededSumInsured, cession.CededShare, cession.CededPremium).ShouldBe((600_000m, 0.375m, 4_500m));
        Cession.CalculateCession(Guid.CreateVersion7(), Risk(900_000m), Surplus, Rule, null, Now).Error!.Code.ShouldBe(ReinsuranceErrorCodes.NothingCeded);
        Cession.CalculateCession(Guid.CreateVersion7(), Risk(900_000m), Excess, Rule, null, Now).Error!.Code.ShouldBe(ReinsuranceErrorCodes.TreatyNotSupported);
        Cession.CalculateCession(Guid.CreateVersion7(), Risk(900_000m), QuotaShare, Rule with { AllocationBasis = AllocationBases.ProbableMaximumLoss }, null, Now)
            .Error!.Code.ShouldBe(ReinsuranceErrorCodes.TreatyNotSupported);
    }

    [Fact]
    public void Recalculation_IsANewCessionSupersedingTheOld_WithThePremiumChange()
    {
        var risk = Risk(2_000_000m);
        var first = Cession.CalculateCession(Guid.CreateVersion7(), risk, QuotaShare, Rule, null, Now).Value;
        var second = Cession.CalculateCession(Guid.CreateVersion7(), risk with { SumInsured = 3_000_000m, Premium = 15_000m }, QuotaShare, Rule, first, Now).Value;
        second.SupersedesId.ShouldBe(first.Id);
        second.DomainEvents.OfType<CessionCalculated>().Single().PremiumChange.ShouldBe(10_500m - 8_400m);

        var facultative = Cession.CalculateFacultative(Guid.CreateVersion7(), risk, new FacultativeArrangement("FAC-1", 1, "GP-1", "RE-2", 40m), null, Now).Value;
        (facultative.CededSumInsured, facultative.ArrangementCode, facultative.RuleCode).ShouldBe((800_000m, "FAC-1", "FACULTATIVE"));
    }

    [Fact]
    public void Recoveries_AreTraceableAndReconcilable_REI003()
    {
        var claim = new SettledClaim("GENERAL", Guid.CreateVersion7(), "GEN-00000001", Guid.CreateVersion7(), 100_000m, "ZAR");
        var proportional = ReinsuranceRecovery.Proportional(Guid.CreateVersion7(), claim, Guid.CreateVersion7(), "QS-2026", 0.7m).Value;
        proportional.Recoverable.ShouldBe(70_000m);
        proportional.SettleRecovery(70_000m, Start, Now).IsFailure.ShouldBeTrue();
        proportional.BillRecovery(Start, Now).IsSuccess.ShouldBeTrue();
        proportional.SettleRecovery(69_500m, Start, Now).IsSuccess.ShouldBeTrue();
        (proportional.Status, proportional.Difference).ShouldBe((RecoveryStatus.Settled, -500m));

        ReinsuranceRecovery.Excess(Guid.CreateVersion7(), claim with { Paid = 400_000m }, Excess).Value.Recoverable.ShouldBe(150_000m);
        ReinsuranceRecovery.Excess(Guid.CreateVersion7(), claim, Excess).Error!.Code.ShouldBe(ReinsuranceErrorCodes.NothingRecoverable);
    }

    [Fact]
    public void Bordereau_ListsTheTreatysCessionsInTheLayoutsFields_ForItsPeriod()
    {
        Bordereau.Period("2026-Q4", "QUARTERLY").Value.ShouldBe((new DateOnly(2026, 10, 1), new DateOnly(2026, 12, 31)));
        Bordereau.Period("2026-11", "MONTHLY").Value.ShouldBe((new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30)));
        Bordereau.Period("2026-Q5", "QUARTERLY").IsFailure.ShouldBeTrue();

        var cession = Cession.CalculateCession(Guid.CreateVersion7(), Risk(2_000_000m), QuotaShare, Rule, null, Now).Value;
        var layout = new BordereauLayout("BX-QS", "QS-2026", ["policy_ref", "ceded_sum_insured", "ceded_premium"], "MONTHLY");
        var bordereau = Bordereau.PrepareBordereau(Guid.CreateVersion7(), layout, "2026-10", [cession]).Value;
        (bordereau.RowCount, bordereau.Content).ShouldBe((1, $"policy_ref,ceded_sum_insured,ceded_premium\n{cession.SourceId},1400000.00,8400.00\n"));
        Bordereau.PrepareBordereau(Guid.CreateVersion7(), layout with { Fields = ["nonsense"] }, "2026-10", []).Error!.Code.ShouldBe(ReinsuranceErrorCodes.LayoutFieldUnknown);
        bordereau.AgreeBordereau().IsFailure.ShouldBeTrue();
        bordereau.SendBordereau("STUB-1", Now).IsSuccess.ShouldBeTrue();
        bordereau.AgreeBordereau().IsSuccess.ShouldBeTrue();
    }
}
