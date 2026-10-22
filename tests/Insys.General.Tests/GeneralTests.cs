using Insys.BuildingBlocks.Domain;
using Insys.General.Domain;
using Insys.General.Domain.Claims;
using Insys.General.Domain.Policies;
using Insys.General.Domain.Underwriting;
using Insys.Testing;

namespace Insys.General.Tests;

public sealed class GeneralTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 21);
    private static readonly DateOnly Start = new(2026, 10, 1);
    private static readonly DateOnly End = new(2027, 10, 1);

    private static readonly ProductTerms Motor = new("MOTOR", 1, "MOTOR", ["OWN_DAMAGE", "THEFT"],
    [
        new("MOT-LIM", "OWN_DAMAGE", "LIMIT", 500_000m, null, "FIXED", "ZAR"),
        new("MOT-EXC", "OWN_DAMAGE", "EXCESS", 5_000m, 5m, "GREATER_OF", "ZAR"),
    ]);

    private static readonly Guid Vehicle = Guid.CreateVersion7();
    private static readonly BillingArrangement Billing = new(Guid.CreateVersion7(), Guid.CreateVersion7(), "MONTHLY", "DEBIT_ORDER");

    private static string Spec(string aggregate, params (string From, string To)[] replacements) =>
        string.Join("|", DomainModelSpec.Transitions("general", aggregate).Select(t => replacements.Aggregate(t, (s, r) => s.Replace(r.From, r.To, StringComparison.Ordinal)))
            .OrderBy(t => t, StringComparer.Ordinal));

    private static string Model<TState, TTrigger>(StateMachine<TState, TTrigger> machine) where TState : struct, Enum where TTrigger : struct, Enum =>
        string.Join("|", machine.Transitions.Select(t => $"{t.From}-{t.Trigger}->{t.To}").OrderBy(t => t, StringComparer.Ordinal));

    private static RiskRequest Risk(decimal ownDamage = 200_000m, string? excess = "MOT-EXC") =>
        new(Vehicle, ownDamage, [new CoverageRequest("OWN_DAMAGE", ownDamage, excess), new CoverageRequest("THEFT", ownDamage / 2, null)]);

    private static GeneralQuote Quote(RiskRequest? risk = null) =>
        GeneralQuote.CreateQuote(Guid.CreateVersion7(), "GQ-1", null, Motor, Guid.CreateVersion7(), null, "ZAR", Start, End, [risk ?? Risk()], Today).Value;

    private static GeneralQuote Accepted(decimal premium = 12_000m)
    {
        var quote = Quote();
        quote.RateQuote(Guid.CreateVersion7(), "MOTOR", 1, 3, premium, "ZAR").IsSuccess.ShouldBeTrue();
        quote.IssueQuote(Today.AddDays(30), Now).IsSuccess.ShouldBeTrue();
        quote.AcceptQuote(Today, Now).IsSuccess.ShouldBeTrue();
        return quote;
    }

    private static GeneralPolicy Policy(decimal premium = 12_000m) => GeneralPolicy.IssuePolicy(Guid.CreateVersion7(), "GP1", Accepted(premium), Billing, null, Today, Now).Value;

    [Fact]
    public void Lifecycles_MatchDomainModel()
    {
        Model(GeneralQuote.Lifecycle).ShouldBe(Spec("GeneralQuote", ("Referral approved", "ReferralApproved"), ("Validity ends", "ValidityEnds")));
        Model(GeneralPolicy.Lifecycle).ShouldBe(Spec("GeneralPolicy", ("Period starts", "PeriodStarts"), ("ReinstatePolicy (where permitted)", "ReinstatePolicy"),
            ("Period ends without renewal", "PeriodEndsWithoutRenewal")));
        Model(GeneralEndorsement.Lifecycle).ShouldBe(Spec("GeneralEndorsement", ("Policy updated", "PolicyUpdated")));
        Model(GeneralClaim.Lifecycle).ShouldBe(Spec("GeneralClaim", ("Payment completed", "PaymentCompleted"), ("Assessment resumes", "AssessmentResumes")));
        Model(LossAssessment.Lifecycle).ShouldBe(Spec("LossAssessment", ("Assessor starts", "AssessorStarts"), ("Further work requested", "FurtherWorkRequested")));
        Model(RecoveryCase.Lifecycle).ShouldBe(Spec("RecoveryCase", ("Demand sent", "DemandSent")));
    }

    [Fact]
    public void AttributeSchema_ValidatesTypesMandatoryFieldsAndRules_GEN005()
    {
        var schema = AttributeSchema.Parse("VEHICLE", 2, ["registration:String(10):Y", "year:Integer:Y", "tracked:Boolean:N"], ["year <= current_year"]).Value;

        schema.Validate(new Dictionary<string, object?> { ["registration"] = "CA123456", ["year"] = 2024m }, 2026).IsSuccess.ShouldBeTrue();
        schema.Validate(new Dictionary<string, object?> { ["year"] = 2024m }, 2026).Error!.Message.ShouldContain("registration is required");
        schema.Validate(new Dictionary<string, object?> { ["registration"] = "CA12345678901", ["year"] = 2024m }, 2026).IsFailure.ShouldBeTrue();
        schema.Validate(new Dictionary<string, object?> { ["registration"] = "CA1", ["year"] = 2024.5m }, 2026).IsFailure.ShouldBeTrue();
        schema.Validate(new Dictionary<string, object?> { ["registration"] = "CA1", ["year"] = 2027m }, 2026).IsFailure.ShouldBeTrue();
        AttributeSchema.Parse("VEHICLE", 2, ["year:Integer:Y"], ["year <= current_year + 1"]).Error!.Code.ShouldBe(GeneralErrorCodes.AttributeSchemaInvalid);
        schema.Validate(new Dictionary<string, object?> { ["registration"] = "CA1", ["year"] = 2024m, ["colour"] = "red" }, 2026).Error!.Message.ShouldContain("colour");
    }

    [Fact]
    public void RiskObject_HasAValuationBasisWheneverValued()
    {
        var schema = AttributeSchema.Parse("VEHICLE", 1, ["registration:String(10):Y"], []).Value;
        var values = new Dictionary<string, object?> { ["registration"] = "CA1" };
        RiskObject.RegisterRiskObject(Guid.CreateVersion7(), schema, values, "{}", 150_000m, null, new(null, null, null), 2026, Now).Error!.Code
            .ShouldBe(GeneralErrorCodes.ValuationBasisRequired);

        var risk = RiskObject.RegisterRiskObject(Guid.CreateVersion7(), schema, values, "{}", 150_000m, "MARKET", new(null, null, "Z1"), 2026, Now).Value;
        risk.AttributeSchemaVersion.ShouldBe(1);
        risk.RevalueRiskObject(10m, " ").IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Quote_StaysWithinProductCoveragesAndLimits()
    {
        GeneralQuote.CreateQuote(Guid.CreateVersion7(), "GQ-1", null, Motor, Guid.CreateVersion7(), null, "ZAR", Start, End, [Risk(600_000m)], Today).Error!.Code
            .ShouldBe(GeneralErrorCodes.OutsideProductLimits);
        GeneralQuote.CreateQuote(Guid.CreateVersion7(), "GQ-1", null, Motor, Guid.CreateVersion7(), null, "ZAR", Start, End,
            [new RiskRequest(Vehicle, 10m, [new CoverageRequest("FLOOD", 10m, null)])], Today).Error!.Code.ShouldBe(GeneralErrorCodes.OutsideProductLimits);
        GeneralQuote.CreateQuote(Guid.CreateVersion7(), "GQ-1", null, Motor, Guid.CreateVersion7(), null, "ZAR", Start, End, [], Today).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Quote_IsRatedOnlyFromAMatchingCalculation_PRD008()
    {
        var quote = Quote();
        quote.RateQuote(Guid.CreateVersion7(), "MOTOR", 2, 3, 100m, "ZAR").Error!.Code.ShouldBe(GeneralErrorCodes.RatingMismatch);
        quote.RateQuote(Guid.CreateVersion7(), "MOTOR", 1, 3, 100m, "USD").Error!.Code.ShouldBe(GeneralErrorCodes.RatingMismatch);

        var calculation = Guid.CreateVersion7();
        quote.RateQuote(calculation, "MOTOR", 1, 3, 100m, "ZAR").IsSuccess.ShouldBeTrue();
        quote.RateQuote(calculation, "MOTOR", 1, 3, 100m, "ZAR").IsSuccess.ShouldBeTrue();
        quote.RatingRequestId.ShouldBe(calculation);
        quote.RateQuote(Guid.CreateVersion7(), "MOTOR", 1, 3, 200m, "ZAR").IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Quote_IsAcceptedOnlyWithinItsValidity_GEN001()
    {
        var quote = Quote();
        quote.RateQuote(Guid.CreateVersion7(), "MOTOR", 1, 3, 100m, "ZAR");
        quote.IssueQuote(Today.AddDays(30), Now);

        quote.AcceptQuote(Today.AddDays(31), Now).Error!.Code.ShouldBe(GeneralErrorCodes.QuoteExpired);
        quote.ExpireQuote(Today.AddDays(30), Now).IsFailure.ShouldBeTrue();
        quote.ExpireQuote(Today.AddDays(31), Now).IsSuccess.ShouldBeTrue();
        quote.Status.ShouldBe(QuoteStatus.Expired);
    }

    [Fact]
    public void ReferredQuote_IsIssuedOnlyAfterTheReferralIsApproved()
    {
        var quote = Quote();
        quote.RateQuote(Guid.CreateVersion7(), "MOTOR", 1, 3, 100m, "ZAR");
        quote.ReferQuote("HIGH_VALUE", Guid.CreateVersion7()).IsSuccess.ShouldBeTrue();
        quote.IssueQuote(Today.AddDays(30), Now).IsFailure.ShouldBeTrue();

        quote.DecideReferral(false, "DECLINE", [], Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        quote.Status.ShouldBe(QuoteStatus.Referred);
        quote.IssueQuote(Today.AddDays(30), Now).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void NewVersion_KeepsTheNumber_AndTheIssuedVersionIsUnchanged()
    {
        var first = Accepted();
        var second = GeneralQuote.CreateQuote(Guid.CreateVersion7(), "IGNORED", first, Motor, first.ProposerPartyId, null, "ZAR", Start, End, [Risk(250_000m)], Today).Value;

        second.QuoteNumber.ShouldBe(first.QuoteNumber);
        second.QuoteVersion.ShouldBe(2);
        first.Risks.Single().SumInsured.ShouldBe(200_000m);
    }

    [Fact]
    public void Policy_PremiumEqualsTheSumOfItsCoveragePremiums()
    {
        var policy = Policy(12_000.01m);

        policy.Coverages.Sum(c => c.Premium).ShouldBe(12_000.01m);
        policy.Coverages.Single(c => c.CoverageCode == "OWN_DAMAGE").Premium.ShouldBe(8_000.01m);
        policy.Status.ShouldBe(PolicyStatus.InForce);
        policy.DomainEvents.OfType<GeneralPolicyIssued>().Single().Billing.ShouldBe(Billing);
    }

    [Fact]
    public void Policy_IsIssuedOnlyFromAnAcceptedQuote_AndInForceOnlyFromItsStart()
    {
        GeneralPolicy.IssuePolicy(Guid.CreateVersion7(), "GP1", Quote(), Billing, null, Today, Now).Error!.Code.ShouldBe(GeneralErrorCodes.QuoteNotAccepted);

        var later = GeneralPolicy.IssuePolicy(Guid.CreateVersion7(), "GP2", Accepted(), Billing, null, Start.AddDays(-1), Now).Value;
        later.Status.ShouldBe(PolicyStatus.Issued);
        later.StartPeriod(Start).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Cancellation_RefundsProRata_LessTheFee_AndIsKeptOnReinstatement_GEN004()
    {
        var policy = Policy(36_500m);
        var cancelled = new DateOnly(2027, 4, 1);
        var unexpired = 36_500m * (End.DayNumber - cancelled.DayNumber) / (End.DayNumber - Start.DayNumber);

        policy.CancelPolicy(new CancellationRule("INS_REQUEST", "INSURED", "PRO_RATA", 100m), cancelled, Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        policy.DomainEvents.OfType<GeneralPolicyCancelled>().Single().RefundAmount.ShouldBe(Math.Round(unexpired, 2, MidpointRounding.AwayFromZero) - 100m);

        policy.ReinstatePolicy(Guid.CreateVersion7(), Today, Now).IsSuccess.ShouldBeTrue();
        policy.Status.ShouldBe(PolicyStatus.InForce);
        policy.Cancellations.Single().ReinstatedAt.ShouldNotBeNull();
        policy.CancellationInEffect.ShouldBeNull();
    }

    [Fact]
    public void Cancellation_WithAnUnspecifiedScale_IsRefused() =>
        Policy().CancelPolicy(new CancellationRule("X", "INSURER", "SHORT_PERIOD", 0), Today, Guid.CreateVersion7(), Now).Error!.Code.ShouldBe(GeneralErrorCodes.RuleNotSpecified);

    [Fact]
    public void Renewal_IsTheNextPeriod_AndThePolicyBecomesRenewed()
    {
        var policy = Policy();
        var renewal = GeneralPolicy.RenewPolicy(Guid.CreateVersion7(), "GP9", policy, Today, Now).Value;

        renewal.PeriodStart.ShouldBe(End);
        renewal.RenewalOfId.ShouldBe(policy.Id);
        renewal.AnnualPremium.ShouldBe(policy.AnnualPremium);
        renewal.Status.ShouldBe(PolicyStatus.Issued);
        policy.MarkRenewed(renewal.Id, Now).IsSuccess.ShouldBeTrue();
        policy.ExpirePolicy(End).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Endorsement_IsEffectiveWithinThePolicyPeriod_GEN003()
    {
        var policy = Policy();
        var type = new EndorsementType("CHANGE_SI", "PRO_RATA", ["ENDORSEMENT"], false);
        List<EndorsementChange> changes = [new(EndorsementAction.ChangeRisk, Vehicle, 300_000m, [new CoverageRequest("OWN_DAMAGE", 300_000m, null)])];

        GeneralEndorsement.DraftEndorsement(Guid.CreateVersion7(), "E1", policy, type, End, changes, Guid.CreateVersion7()).Error!.Code.ShouldBe(GeneralErrorCodes.OutsidePolicyPeriod);
        GeneralEndorsement.DraftEndorsement(Guid.CreateVersion7(), "E1", policy, type, Start.AddDays(-1), changes, Guid.CreateVersion7()).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Endorsement_IsAppliedOnlyAfterASecondPersonApproves_SRS002()
    {
        var policy = Policy(12_000m);
        var maker = Guid.CreateVersion7();
        var type = new EndorsementType("CHANGE_SI", "PRO_RATA", ["ENDORSEMENT"], false);
        var effective = new DateOnly(2027, 4, 1);
        var endorsement = GeneralEndorsement.DraftEndorsement(Guid.CreateVersion7(), "E1", policy, type, effective,
            [new(EndorsementAction.ChangeRisk, Vehicle, 300_000m, [new CoverageRequest("OWN_DAMAGE", 300_000m, null)])], maker).Value;

        endorsement.RateEndorsement(Guid.CreateVersion7(), 15_000m, policy, type).IsSuccess.ShouldBeTrue();
        endorsement.PremiumDelta.ShouldBe(Math.Round(3_000m * (End.DayNumber - effective.DayNumber) / (End.DayNumber - Start.DayNumber), 2, MidpointRounding.AwayFromZero));
        policy.ApplyEndorsement(endorsement, Motor, [], Now).IsFailure.ShouldBeTrue();

        endorsement.SubmitEndorsement(maker, "ZAR", Now).IsSuccess.ShouldBeTrue();
        endorsement.Decide(true, Guid.CreateVersion7(), maker, Now).Error!.Code.ShouldBe(ErrorCodes.MakerCheckerSamePerson);
        endorsement.Decide(true, Guid.CreateVersion7(), Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();

        policy.ApplyEndorsement(endorsement, Motor, ["ENDORSEMENT"], Now).IsSuccess.ShouldBeTrue();
        policy.AnnualPremium.ShouldBe(15_000m);
        policy.Coverages.Sum(c => c.Premium).ShouldBe(15_000m);
        policy.Coverages.Single(c => c.CoverageCode == "OWN_DAMAGE").SumInsured.ShouldBe(300_000m);
        endorsement.MarkApplied().IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Endorsement_BeyondProductLimits_IsNotApplied()
    {
        var policy = Policy();
        var type = new EndorsementType("CHANGE_SI", "FLAT", [], false);
        var endorsement = GeneralEndorsement.DraftEndorsement(Guid.CreateVersion7(), "E1", policy, type, Today,
            [new(EndorsementAction.ChangeRisk, Vehicle, null, [new CoverageRequest("OWN_DAMAGE", 900_000m, null)])], Guid.CreateVersion7()).Value;
        endorsement.RateEndorsement(Guid.CreateVersion7(), 20_000m, policy, type);
        endorsement.SubmitEndorsement(Guid.CreateVersion7(), "ZAR", Now);
        endorsement.Decide(true, Guid.CreateVersion7(), Guid.CreateVersion7(), Now);

        policy.ApplyEndorsement(endorsement, Motor, [], Now).Error!.Code.ShouldBe(GeneralErrorCodes.OutsideProductLimits);
    }

    [Fact]
    public void Claim_IsRegisteredOnlyWithValidCover_GEN006()
    {
        var policy = Policy();
        GeneralClaim Notify(DateOnly loss, string coverage = "OWN_DAMAGE", Guid? risk = null) =>
            GeneralClaim.NotifyLoss(Guid.CreateVersion7(), policy.Id, risk ?? Vehicle, coverage, loss, "COLLISION", "ACC", null, null, Today, Now).Value;

        Notify(Start.AddDays(-1)).RegisterClaim(policy, "GEN-1", Now).Error!.Code.ShouldBe(GeneralErrorCodes.NotCovered);
        Notify(Today, "FLOOD").RegisterClaim(policy, "GEN-1", Now).Error!.Code.ShouldBe(GeneralErrorCodes.NotCovered);
        Notify(Today, risk: Guid.CreateVersion7()).RegisterClaim(policy, "GEN-1", Now).Error!.Code.ShouldBe(GeneralErrorCodes.NotCovered);
        GeneralClaim.NotifyLoss(Guid.CreateVersion7(), policy.Id, Vehicle, "OWN_DAMAGE", Today.AddDays(1), "C", "P", null, null, Today, Now).IsFailure.ShouldBeTrue();

        policy.CancelPolicy(new CancellationRule("INS_REQUEST", "INSURED", "NONE", 0), Today, Guid.CreateVersion7(), Now);
        Notify(Today).RegisterClaim(policy, "GEN-1", Now).Error!.Code.ShouldBe(GeneralErrorCodes.NotCovered);
        Notify(Today.AddDays(-1)).RegisterClaim(policy, "GEN-1", Now).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Excess_FollowsItsRule_GEN008()
    {
        List<CoverageLimit> greaterOf = [new("S", "OD", "EXCESS", 5_000m, 5m, "GREATER_OF", "ZAR")];
        ExcessCalculator.Excess(greaterOf, 40_000m).ShouldBe(5_000m);
        ExcessCalculator.Excess(greaterOf, 200_000m).ShouldBe(10_000m);
        ExcessCalculator.Excess([new("S", "OD", "DEDUCTIBLE", 5_000m, 5m, "LESSER_OF", "ZAR")], 40_000m).ShouldBe(2_000m);
        ExcessCalculator.Excess(greaterOf, 3_000m).ShouldBe(3_000m);
        ExcessCalculator.Settlement(250_000m, 12_500m, 200_000m, [new("L", "OD", "SUBLIMIT", 150_000m, null, "FIXED", "ZAR")]).ShouldBe(150_000m);
        ExcessCalculator.Settlement(40_000m, 5_000m, 200_000m, []).ShouldBe(35_000m);
    }

    [Fact]
    public void Settlement_NeedsASecondPersonsApproval_AndRecordsItsRationale_SRS002_CLM003()
    {
        var policy = Policy();
        var claim = GeneralClaim.NotifyLoss(Guid.CreateVersion7(), policy.Id, Vehicle, "OWN_DAMAGE", Today, "COLLISION", "ACC", null, null, Today, Now).Value;
        claim.RegisterClaim(policy, "GEN-1", Now);
        claim.AppointAssessor(Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        var coverage = policy.Coverage(Vehicle, "OWN_DAMAGE")!;
        var maker = Guid.CreateVersion7();

        claim.RequestSettlement(new SettlementRequest(40_000m, 5_000m, Guid.CreateVersion7(), "INDEMNITY", "SETTLE", " "), coverage, [], "ZAR", maker, Now).Error!.Code
            .ShouldBe(GeneralErrorCodes.RationaleRequired);
        claim.RequestSettlement(new SettlementRequest(4_000m, 5_000m, Guid.CreateVersion7(), "INDEMNITY", "SETTLE", "Excess exceeds loss"), coverage, [], "ZAR", maker, Now)
            .Error!.Code.ShouldBe(GeneralErrorCodes.NothingToSettle);
        claim.RequestSettlement(new SettlementRequest(40_000m, 5_000m, Guid.CreateVersion7(), "INDEMNITY", "SETTLE", "Repair quote accepted"), coverage, [], "ZAR", maker, Now)
            .IsSuccess.ShouldBeTrue();
        claim.SettlementAmount.ShouldBe(35_000m);
        claim.Status.ShouldBe(ClaimStatus.UnderAssessment);

        claim.DecideSettlement(true, Guid.CreateVersion7(), maker, Now).Error!.Code.ShouldBe(ErrorCodes.MakerCheckerSamePerson);
        claim.DecideSettlement(false, Guid.CreateVersion7(), Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        claim.Status.ShouldBe(ClaimStatus.UnderAssessment);
        claim.SettlementAmount.ShouldBeNull();

        claim.RequestSettlement(new SettlementRequest(40_000m, 5_000m, Guid.CreateVersion7(), "INDEMNITY", "SETTLE", "Repair quote accepted"), coverage, [], "ZAR", maker, Now);
        claim.DecideSettlement(true, Guid.CreateVersion7(), Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        claim.Status.ShouldBe(ClaimStatus.Approved);
        claim.DomainEvents.OfType<GeneralClaimApproved>().Single().Amount.ShouldBe(35_000m);

        claim.SettleClaim(Today, Now).IsSuccess.ShouldBeTrue();
        claim.CloseClaim(Now).IsSuccess.ShouldBeTrue();
        claim.ReopenClaim().IsSuccess.ShouldBeTrue();
        claim.AppointAssessor(Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        claim.Status.ShouldBe(ClaimStatus.UnderAssessment);
    }

    [Fact]
    public void Decline_RecordsItsRationale_CLM003()
    {
        var policy = Policy();
        var claim = GeneralClaim.NotifyLoss(Guid.CreateVersion7(), policy.Id, Vehicle, "OWN_DAMAGE", Today, "COLLISION", "ACC", null, null, Today, Now).Value;
        claim.RegisterClaim(policy, "GEN-1", Now);
        claim.DeclineClaim("EXCLUDED", "Driver unlicensed", Guid.CreateVersion7(), Now).IsFailure.ShouldBeTrue();
        claim.AppointAssessor(Guid.CreateVersion7(), Now);

        claim.DeclineClaim("EXCLUDED", "", Guid.CreateVersion7(), Now).Error!.Code.ShouldBe(GeneralErrorCodes.RationaleRequired);
        claim.DeclineClaim("EXCLUDED", "Driver unlicensed", Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        claim.DecisionRationale.ShouldBe("Driver unlicensed");
    }

    [Fact]
    public void Assessment_NeedsAPanelAssessor_GEN007_AndReportsWithinItsLifecycle()
    {
        LossAssessment.AssignAssessor(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), onApprovedPanel: false).Error!.Code
            .ShouldBe(GeneralErrorCodes.AssessorNotOnPanel);

        var assessment = LossAssessment.AssignAssessor(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), onApprovedPanel: true).Value;
        assessment.RecordEstimate(Guid.CreateVersion7(), 10m).IsFailure.ShouldBeTrue();
        assessment.Start().IsSuccess.ShouldBeTrue();
        assessment.RecordEstimate(Guid.CreateVersion7(), 38_000m).IsSuccess.ShouldBeTrue();
        assessment.SubmitReport(1_000m, 2_000m, null, Now).IsFailure.ShouldBeTrue();
        assessment.SubmitReport(40_000m, 5_000m, null, Now).IsSuccess.ShouldBeTrue();
        assessment.RequestFurtherWork().IsSuccess.ShouldBeTrue();
        assessment.SubmitReport(42_000m, 5_000m, null, Now).IsSuccess.ShouldBeTrue();
        assessment.AcceptAssessment(Guid.CreateVersion7()).IsSuccess.ShouldBeTrue();
        assessment.AssessedAmount.ShouldBe(42_000m);
    }

    [Fact]
    public void Recovery_IsRecordedForTheClaimsFramework_CLM004()
    {
        var recovery = RecoveryCase.OpenRecovery(Guid.CreateVersion7(), Guid.CreateVersion7(), "SUBROGATION", Guid.CreateVersion7(), 20_000m).Value;
        recovery.RecordRecovery(15_000m, "GEN-1", "ZAR", "INDEMNITY", Now).IsFailure.ShouldBeTrue();
        recovery.SendDemand().IsSuccess.ShouldBeTrue();
        recovery.AddSalvageItem("Vehicle wreck").IsSuccess.ShouldBeTrue();
        recovery.DisposeSalvage(recovery.Salvage.Single().Id, "AUCTION", 3_000m).IsSuccess.ShouldBeTrue();

        recovery.RecordRecovery(15_000m, "GEN-1", "ZAR", "INDEMNITY", Now).IsSuccess.ShouldBeTrue();
        recovery.DomainEvents.OfType<RecoveryRecorded>().Single().ShouldSatisfyAllConditions(e => e.Amount.ShouldBe(15_000m), e => e.ClaimNumber.ShouldBe("GEN-1"));
        recovery.CloseRecovery().IsSuccess.ShouldBeTrue();
        recovery.AddSalvageItem("late").IsFailure.ShouldBeTrue();
    }
}
