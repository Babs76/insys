using Insys.BuildingBlocks.Domain;
using Insys.Life.Domain;
using Insys.Life.Domain.Claims;
using Insys.Life.Domain.Groups;
using Insys.Life.Domain.Policies;
using Insys.Life.Domain.Proposals;
using Insys.Life.Domain.Underwriting;
using Insys.Testing;

namespace Insys.Life.Tests;

public sealed class LifeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 22);
    private static readonly Guid Applicant = Guid.CreateVersion7();
    private static readonly Guid Spouse = Guid.CreateVersion7();
    private static readonly Guid Maker = Guid.CreateVersion7();
    private static readonly Guid Checker = Guid.CreateVersion7();
    private static readonly LifeProductTerms Term = new("LIF-TERM", 1, ["DEATH", "DISABILITY"]);
    private static readonly GraceRule Grace = new(30, true);
    private static readonly LapseRule Lapse = new(2, "P6M", "DECLARATION");

    private static string Spec(string aggregate, params (string From, string To)[] replacements) =>
        string.Join("|", DomainModelSpec.Transitions("life", aggregate).Select(t => replacements.Aggregate(t, (s, r) => s.Replace(r.From, r.To, StringComparison.Ordinal)))
            .OrderBy(t => t, StringComparer.Ordinal));

    private static string Model<TState, TTrigger>(StateMachine<TState, TTrigger> machine) where TState : struct, Enum where TTrigger : struct, Enum =>
        string.Join("|", machine.Transitions.Select(t => $"{t.From}-{t.Trigger}->{t.To}").OrderBy(t => t, StringComparer.Ordinal));

    private static Result<LifeProposal> Capture(IReadOnlyList<BeneficiaryNomination>? nominations = null, NominationRule? rule = null, DateOnly? inception = null) =>
        LifeProposal.CaptureProposal(Guid.CreateVersion7(), "LP1", Term, Applicant, null, "BRK_PORTAL", "ZAR", inception ?? Today,
            new ProposalBilling(Applicant, "MONTHLY", "DEBIT_ORDER"), [new InsuredLifeRequest(Applicant, "MAIN", 41, "NS", null)],
            [new CoverRequest(Applicant, "DEATH", 1_000_000m, 20), new CoverRequest(Applicant, "DISABILITY", 500_000m, 20)], [],
            nominations ?? [new BeneficiaryNomination(Spouse, "SPOUSE", 100m)], rule);

    private static LifeProposal Quoted(decimal premium = 450m)
    {
        var proposal = Capture().Value;
        proposal.QuotePremium(Guid.CreateVersion7(), "LIF-TERM", 1, 3, premium, "ZAR").IsSuccess.ShouldBeTrue();
        proposal.RecordEligibility(Guid.CreateVersion7(), "ELIG-TERM", 2, LifeProposal.Eligible).IsSuccess.ShouldBeTrue();
        return proposal;
    }

    private static (LifeProposal Proposal, LifeUnderwritingCase Case) Decided(UnderwritingOutcome outcome = UnderwritingOutcome.Accept,
        IReadOnlyList<DecisionItemRequest>? items = null)
    {
        var proposal = Quoted();
        proposal.SubmitProposal(Now).IsSuccess.ShouldBeTrue();
        var underwriting = LifeUnderwritingCase.OpenCase(Guid.CreateVersion7(), proposal, Now).Value;
        proposal.OpenUnderwriting().IsSuccess.ShouldBeTrue();
        underwriting.StartAssessment().IsSuccess.ShouldBeTrue();
        underwriting.RequestDecision(outcome, "STD", "STANDARD", items ?? [], new HashSet<string> { "LOADED" }, proposal.TotalSumAssured, "ZAR", Maker, Now)
            .IsSuccess.ShouldBeTrue();
        underwriting.Decide(true, Guid.CreateVersion7(), Checker, underwriting.SubjectHash, "LIFE_UNDERWRITING", Now).IsSuccess.ShouldBeTrue();
        proposal.ApplyDecision(outcome, Now).IsSuccess.ShouldBeTrue();
        return (proposal, underwriting);
    }

    private static LifePolicy Policy(DateOnly? inception = null)
    {
        var proposal = Capture(inception: inception).Value;
        proposal.QuotePremium(Guid.CreateVersion7(), "LIF-TERM", 1, 3, 450m, "ZAR");
        proposal.RecordEligibility(Guid.CreateVersion7(), "ELIG-TERM", 2, LifeProposal.Eligible);
        proposal.SubmitProposal(Now);
        var underwriting = LifeUnderwritingCase.OpenCase(Guid.CreateVersion7(), proposal, Now).Value;
        proposal.OpenUnderwriting();
        underwriting.StartAssessment();
        underwriting.RequestDecision(UnderwritingOutcome.Accept, "STD", "STANDARD", [], new HashSet<string>(), 1m, "ZAR", Maker, Now);
        underwriting.Decide(true, Guid.CreateVersion7(), Checker, underwriting.SubjectHash, null, Now);
        proposal.ApplyDecision(UnderwritingOutcome.Accept, Now);
        return LifePolicy.IssuePolicy(Guid.CreateVersion7(), "LP0000001", proposal, underwriting, Guid.CreateVersion7(), Today, Now).Value;
    }

    [Fact]
    public void Lifecycles_MatchDomainModel()
    {
        Model(LifeProposal.Lifecycle).ShouldBe(Spec("LifeProposal", ("Case opened", "CaseOpened"), ("Decision: accept", "DecisionAccept"),
            ("Decision: decline", "DecisionDecline"), ("Decision: postpone", "DecisionPostpone"), ("Policy issued", "PolicyIssued")));
        Model(LifeUnderwritingCase.Lifecycle).ShouldBe(Spec("LifeUnderwritingCase", ("Evidence complete", "EvidenceComplete"), ("No evidence needed", "NoEvidenceNeeded"),
            ("Referral answered", "ReferralAnswered")));
        Model(LifePolicy.Lifecycle).ShouldBe(Spec("LifePolicy", ("Inception date reached", "InceptionDateReached"),
            ("Reinstatement window passed", "ReinstatementWindowPassed")));
        Model(LifePolicyLoan.Lifecycle).ShouldBe(Spec("LifePolicyLoan", ("Balance reaches zero", "BalanceReachesZero"),
            ("Claim or surrender settles it", "ClaimOrSurrenderSettles")));
        Model(LifeClaim.Lifecycle).ShouldBe(Spec("LifeClaim", ("Payment completed", "PaymentCompleted")));
        Model(GroupScheme.Lifecycle).ShouldBe(Spec("GroupScheme", ("Start date reached", "StartDateReached")));
    }

    [Fact]
    public void BeneficiaryNominations_TotalOneHundredPercent_WithinTheProductsNominationRule()
    {
        Capture([new BeneficiaryNomination(Spouse, "SPOUSE", 60m), new BeneficiaryNomination(Guid.CreateVersion7(), "CHILD", 30m)]).Error!.Code
            .ShouldBe(LifeErrorCodes.BeneficiarySharesInvalid);
        Capture([new BeneficiaryNomination(Spouse, "SPOUSE", 60m), new BeneficiaryNomination(Guid.CreateVersion7(), "FRIEND", 40m)], new NominationRule(5, 10m, ["SPOUSE", "CHILD"]))
            .Error!.Code.ShouldBe(LifeErrorCodes.NominationRuleBreached);
        Capture([new BeneficiaryNomination(Spouse, "SPOUSE", 60m), new BeneficiaryNomination(Guid.CreateVersion7(), "CHILD", 40m)], new NominationRule(5, 10m, ["SPOUSE", "CHILD"]))
            .IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Proposal_CoversOnlyTheProductsBenefits()
    {
        var proposal = LifeProposal.CaptureProposal(Guid.CreateVersion7(), "LP1", Term, Applicant, null, "BRK", "ZAR", Today, new ProposalBilling(Applicant, "MONTHLY", "DO"),
            [new InsuredLifeRequest(Applicant, "MAIN", 41, "NS", null)], [new CoverRequest(Applicant, "CRITICAL_ILLNESS", 1m, 10)], [], [], null);
        proposal.Error!.Code.ShouldBe(LifeErrorCodes.BenefitNotInProduct);
    }

    [Fact]
    public void QuotePremium_SpreadsTheQuoteOverCoversBySumAssured_AndRejectsAnotherProduct_LIF001()
    {
        var proposal = Capture().Value;
        proposal.QuotePremium(Guid.CreateVersion7(), "OTHER", 1, 3, 450m, "ZAR").Error!.Code.ShouldBe(LifeErrorCodes.RatingMismatch);
        proposal.QuotePremium(Guid.CreateVersion7(), "LIF-TERM", 1, 3, 450.01m, "ZAR").IsSuccess.ShouldBeTrue();

        proposal.Status.ShouldBe(ProposalStatus.Quoted);
        proposal.Covers.Select(c => c.Premium).ShouldBe([300.01m, 150m]);
    }

    [Fact]
    public void SubmitProposal_NeedsAnEligibleRuleDecision_AndThenTheContentIsFrozen_LIF001()
    {
        var proposal = Capture().Value;
        proposal.QuotePremium(Guid.CreateVersion7(), "LIF-TERM", 1, 3, 450m, "ZAR");
        proposal.SubmitProposal(Now).Error!.Code.ShouldBe(LifeErrorCodes.ProposalNotEligible);
        proposal.RecordEligibility(Guid.CreateVersion7(), "ELIG-TERM", 2, "INELIGIBLE");
        proposal.SubmitProposal(Now).Error!.Code.ShouldBe(LifeErrorCodes.ProposalNotEligible);
        proposal.RecordEligibility(Guid.CreateVersion7(), "ELIG-TERM", 2, LifeProposal.Eligible);

        proposal.SubmitProposal(Now).IsSuccess.ShouldBeTrue();
        proposal.DomainEvents.OfType<Domain.Proposals.LifeProposalSubmitted>().ShouldHaveSingleItem();
        proposal.QuotePremium(Guid.CreateVersion7(), "LIF-TERM", 1, 3, 500m, "ZAR").Error!.Code.ShouldBe(LifeErrorCodes.ProposalFrozen);
    }

    [Fact]
    public void UnderwritingCase_RecordsTheRuleSetVersion_LIF002_AndWaitsForMandatoryEvidence()
    {
        var proposal = Quoted();
        proposal.SubmitProposal(Now);
        var underwriting = LifeUnderwritingCase.OpenCase(Guid.CreateVersion7(), proposal, Now).Value;
        (underwriting.RulesetCode, underwriting.RulesetVersion).ShouldBe(("ELIG-TERM", 2));

        var medical = underwriting.RequestEvidence("MEDICAL", true, Now).Value;
        var optional = underwriting.RequestEvidence("GP_REPORT", false, Now).Value;
        underwriting.Status.ShouldBe(CaseStatus.AwaitingEvidence);
        underwriting.RecordEvidence(optional.Id, Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        underwriting.Status.ShouldBe(CaseStatus.AwaitingEvidence);
        underwriting.WaiveEvidence(medical.Id, "Recent cover with us", Maker).IsSuccess.ShouldBeTrue();
        underwriting.Status.ShouldBe(CaseStatus.InAssessment);
    }

    [Fact]
    public void Decision_IsApprovedByAnotherPerson_ForTheSameTerms_SRS002()
    {
        var proposal = Quoted();
        proposal.SubmitProposal(Now);
        var underwriting = LifeUnderwritingCase.OpenCase(Guid.CreateVersion7(), proposal, Now).Value;
        underwriting.StartAssessment();
        underwriting.RequestDecision(UnderwritingOutcome.Accept, "LOADED", "SMOKER", [new DecisionItemRequest("DEATH", "LOADED", null, null, null, null)],
            new HashSet<string> { "LOADED" }, 1m, "ZAR", Maker, Now).Error!.Code.ShouldBe(LifeErrorCodes.TermsRequired);
        underwriting.RequestDecision(UnderwritingOutcome.Accept, "STD", "STANDARD", [new DecisionItemRequest("DEATH", "EXCL", null, null, "EXC-AVIATION", null)],
            new HashSet<string>(), 1m, "ZAR", Maker, Now).IsSuccess.ShouldBeTrue();
        var requested = underwriting.DomainEvents.OfType<UnderwritingDecisionRequested>().Single();

        underwriting.Decide(true, Guid.CreateVersion7(), Maker, requested.SubjectHash, null, Now).Error!.Code.ShouldBe(ErrorCodes.MakerCheckerSamePerson);
        underwriting.Decide(true, Guid.CreateVersion7(), Checker, "other", null, Now).Error!.Code.ShouldBe(LifeErrorCodes.ApprovalSubjectChanged);
        underwriting.Decide(true, Guid.CreateVersion7(), Checker, requested.SubjectHash, null, Now).IsSuccess.ShouldBeTrue();
        underwriting.Status.ShouldBe(CaseStatus.Decided);
        underwriting.DomainEvents.OfType<Domain.Underwriting.LifeUnderwritingDecided>().Single().RulesetVersion.ShouldBe(2);
    }

    [Fact]
    public void IssuePolicy_ReferencesTheProductAndRatingVersions_CarriesExclusions_AndBeneficiaries_LIF003()
    {
        var (proposal, underwriting) = Decided(items: [new DecisionItemRequest("DEATH", "EXCL", null, null, "EXC-AVIATION", null)]);
        var policy = LifePolicy.IssuePolicy(Guid.CreateVersion7(), "LP0000001", proposal, underwriting, Guid.CreateVersion7(), Today, Now).Value;

        (policy.ProductCode, policy.ProductVersion, policy.RatingVersion, policy.RulesetVersion).ShouldBe(("LIF-TERM", 1, 3, 2));
        policy.Status.ShouldBe(LifePolicyStatus.InForce);
        policy.Benefits.Single(b => b.BenefitCode == "DEATH").ExclusionCodes.ShouldBe(["EXC-AVIATION"]);
        policy.Benefits.Single(b => b.BenefitCode == "DEATH").EndDate.ShouldBe(Today.AddYears(20).AddDays(-1));
        policy.BeneficiariesOn(Today).Single().SharePct.ShouldBe(100m);
        policy.Schedule.Premium.ShouldBe(450m);
    }

    [Fact]
    public void IssuePolicy_WithALoading_IsRefusedUntilLoadingsCanBeRated_OQ052()
    {
        var (proposal, underwriting) = Decided(items: [new DecisionItemRequest("DEATH", "LOADED", 50m, null, null, null)]);

        LifePolicy.IssuePolicy(Guid.CreateVersion7(), "LP0000001", proposal, underwriting, Guid.CreateVersion7(), Today, Now).Error!.Code
            .ShouldBe(LifeErrorCodes.LoadedTermsNotRated);
    }

    [Fact]
    public void ChangeBeneficiaries_KeepsHistory_AndSharesTotalOneHundredOnEveryDate()
    {
        var policy = Policy();
        var child = Guid.CreateVersion7();
        policy.ChangeBeneficiaries([new BeneficiaryNomination(Spouse, "SPOUSE", 50m), new BeneficiaryNomination(child, "CHILD", 40m)], Today.AddDays(10), null, Maker, Now)
            .Error!.Code.ShouldBe(LifeErrorCodes.BeneficiarySharesInvalid);
        policy.ChangeBeneficiaries([new BeneficiaryNomination(Spouse, "SPOUSE", 50m), new BeneficiaryNomination(child, "CHILD", 50m)], Today.AddDays(10), null, Maker, Now)
            .IsSuccess.ShouldBeTrue();

        policy.BeneficiariesOn(Today.AddDays(9)).Sum(b => b.SharePct).ShouldBe(100m);
        policy.BeneficiariesOn(Today.AddDays(10)).Select(b => b.SharePct).ShouldBe([50m, 50m]);
        policy.Events.Single().EventType.ShouldBe("BENEFICIARIES");
    }

    [Fact]
    public void AlterPolicy_IsCheckedAgainstTheStatesTheAlterationTypeAllows_LIF004()
    {
        var policy = Policy();
        policy.AlterPolicy(new AlterationType("CHG_AGENT", ["LAPSED"], false, "SERVICING", "LETTER"), "BRK9", Today, Maker, Now).Error!.Code
            .ShouldBe(LifeErrorCodes.AlterationNotAllowed);
        policy.AlterPolicy(new AlterationType("INC_COVER", ["IN_FORCE"], true, "UNDERWRITER", "LETTER"), "BRK9", Today, Maker, Now).Error!.Code
            .ShouldBe(LifeErrorCodes.AlterationNotAllowed);
        policy.AlterPolicy(new AlterationType("CHG_AGENT", ["IN_FORCE"], false, "SERVICING", "LETTER"), "BRK9", Today, Maker, Now).IsSuccess.ShouldBeTrue();
        policy.IntermediaryCode.ShouldBe("BRK9");
    }

    [Fact]
    public void MissedPremiums_StartGrace_AndLapseOnlyAfterGraceWithEnoughMissed_LIF005()
    {
        var policy = Policy(Today.AddMonths(-3));
        var firstUnpaid = Today.AddMonths(-2);
        policy.RecordArrears(1, firstUnpaid, Grace);
        policy.Schedule.GraceUntil.ShouldBe(firstUnpaid.AddDays(30));
        policy.LapsePolicy(Lapse, Today, Now).IsFailure.ShouldBeTrue();

        policy.RecordArrears(2, firstUnpaid, Grace);
        policy.LapsePolicy(Lapse, firstUnpaid.AddDays(30), Now).IsFailure.ShouldBeTrue();
        policy.LapsePolicy(Lapse, Today, Now).IsSuccess.ShouldBeTrue();
        policy.LapsedOn.ShouldBe(firstUnpaid.AddDays(31));
        policy.DomainEvents.OfType<Domain.Policies.LifePolicyLapsed>().Single().MissedPayments.ShouldBe(2);
    }

    [Fact]
    public void APaymentInGrace_ClearsTheArrears()
    {
        var policy = Policy(Today.AddMonths(-3));
        policy.RecordArrears(2, Today.AddMonths(-2), Grace);
        policy.RecordArrears(0, null, Grace);

        (policy.Schedule.MissedCount, policy.Schedule.GraceUntil).ShouldBe((0, (DateOnly?)null));
        policy.LapsePolicy(Lapse, Today, Now).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Reinstatement_IsWithinTheWindow_WithEvidenceWhereRequired_ThenTheWindowEnds_LIF005()
    {
        var policy = Policy(Today.AddMonths(-3));
        policy.RecordArrears(2, Today.AddMonths(-2), Grace);
        policy.LapsePolicy(Lapse, Today, Now);
        var lapsedOn = policy.LapsedOn!.Value;

        policy.ReinstatePolicy(Lapse, Today, null, Maker, Now).Error!.Code.ShouldBe(LifeErrorCodes.ReinstatementNotPermitted);
        policy.ReinstatePolicy(Lapse, lapsedOn.AddMonths(7), Guid.CreateVersion7(), Maker, Now).Error!.Code.ShouldBe(LifeErrorCodes.ReinstatementNotPermitted);
        policy.TerminateLapsed(Lapse, Today, Now).IsFailure.ShouldBeTrue();
        policy.ReinstatePolicy(Lapse, Today, Guid.CreateVersion7(), Maker, Now).IsSuccess.ShouldBeTrue();
        (policy.Status, policy.Schedule.MissedCount).ShouldBe((LifePolicyStatus.InForce, 0));

        var other = Policy(Today.AddMonths(-3));
        other.RecordArrears(2, Today.AddMonths(-2), Grace);
        other.LapsePolicy(Lapse, Today, Now);
        other.TerminateLapsed(Lapse, other.LapsedOn!.Value.AddMonths(6).AddDays(1), Now).IsSuccess.ShouldBeTrue();
        other.Status.ShouldBe(LifePolicyStatus.Terminated);
    }

    [Fact]
    public void CoversEvent_ChecksStatusBenefitInsuredLifeAndGraceCover_LIF008()
    {
        var policy = Policy(Today.AddMonths(-3));
        var life = policy.Lives.Single().Id;
        policy.CoversEvent(life, "DEATH", Today, Grace).IsSuccess.ShouldBeTrue();
        policy.CoversEvent(Guid.CreateVersion7(), "DEATH", Today, Grace).IsFailure.ShouldBeTrue();
        policy.CoversEvent(life, "CRITICAL_ILLNESS", Today, Grace).IsFailure.ShouldBeTrue();
        policy.CoversEvent(life, "DEATH", Today.AddMonths(-4), Grace).IsFailure.ShouldBeTrue();

        policy.RecordArrears(1, Today.AddDays(-10), Grace);
        policy.CoversEvent(life, "DEATH", Today, new GraceRule(30, CoverDuringGrace: false)).Error!.Code.ShouldBe(LifeErrorCodes.NotCovered);
    }

    [Fact]
    public void ClaimApproval_SplitsTheNetAmountBySharesAfterTheLoan_AndIsBoundToItsSubject_CLM003()
    {
        var policy = Policy(Today.AddMonths(-3));
        var claim = LifeClaim.NotifyClaim(Guid.CreateVersion7(), policy, "DEATH", Today, "I21", Spouse, policy.Lives.Single().Id, Now).Value;
        claim.RegisterClaim("CLM-LIFE-1", Now);
        var death = policy.Benefits.Single(b => b.BenefitCode == "DEATH");
        claim.AssessClaim([new BenefitLineRequest(death.Id, 1_000_001m)], new Dictionary<Guid, decimal> { [death.Id] = death.SumAssured }).IsFailure.ShouldBeTrue();
        claim.AssessClaim([new BenefitLineRequest(death.Id, 1_000_000m)], new Dictionary<Guid, decimal> { [death.Id] = death.SumAssured }).IsSuccess.ShouldBeTrue();

        var child = Guid.CreateVersion7();
        claim.RequestApproval([new PayeeRequest(Spouse, Guid.CreateVersion7(), "h1", 66.67m), new PayeeRequest(child, Guid.CreateVersion7(), "h2", 33.33m)], 100_000m,
            "INDEMNITY", "DEATH_PROVEN", "", Maker, Now).Error!.Code.ShouldBe(LifeErrorCodes.RationaleRequired);
        claim.RequestApproval([new PayeeRequest(Spouse, Guid.CreateVersion7(), "h1", 66.67m), new PayeeRequest(child, Guid.CreateVersion7(), "h2", 33.33m)], 100_000m,
            "INDEMNITY", "DEATH_PROVEN", "Death certificate and ID verified", Maker, Now).IsSuccess.ShouldBeTrue();

        claim.ApprovedAmount.ShouldBe(900_000m);
        claim.Payees.Select(p => p.Amount).ShouldBe([600_030m, 299_970m]);
        claim.DecideApproval(true, Guid.CreateVersion7(), Checker, "stale", Now).Error!.Code.ShouldBe(LifeErrorCodes.ApprovalSubjectChanged);
        claim.DecideApproval(true, Guid.CreateVersion7(), Checker, claim.SubjectHash, Now).IsSuccess.ShouldBeTrue();
        claim.DomainEvents.OfType<Domain.Claims.LifeClaimApproved>().Single().LoanOffset.ShouldBe(100_000m);

        claim.RecordPayment(600_030m, Today, Now);
        claim.Status.ShouldBe(LifeClaimStatus.Approved);
        claim.RecordPayment(299_970m, Today, Now);
        claim.Status.ShouldBe(LifeClaimStatus.Settled);
        policy.CloseOnClaim([death.Id], Today, Now);
        policy.Status.ShouldBe(LifePolicyStatus.InForce);
        policy.CloseOnClaim(policy.Benefits.Select(b => b.Id).ToList(), Today, Now);
        policy.Status.ShouldBe(LifePolicyStatus.Claimed);
    }

    [Fact]
    public void Loans_AreForProductsThatSupportThem_WithinTheShareOfCashValue_AndRepaid_LIF007()
    {
        var policy = Policy(Today.AddYears(-3));
        var rule = new LoanRule(80m, "P2Y");
        LifePolicyLoan.IssueLoan(Guid.CreateVersion7(), "L1", policy, null, 100_000m, 0, 10_000m, "LR1", Today, Now).Error!.Code.ShouldBe(LifeErrorCodes.LoanNotSupported);
        LifePolicyLoan.IssueLoan(Guid.CreateVersion7(), "L1", policy, rule, 100_000m, 75_000m, 10_000m, "LR1", Today, Now).Error!.Code
            .ShouldBe(LifeErrorCodes.LoanExceedsCashValue);
        LifePolicyLoan.IssueLoan(Guid.CreateVersion7(), "L1", Policy(Today.AddYears(-1)), rule, 100_000m, 0, 10_000m, "LR1", Today, Now).Error!.Code
            .ShouldBe(LifeErrorCodes.LoanNotSupported);

        var loan = LifePolicyLoan.IssueLoan(Guid.CreateVersion7(), "L1", policy, rule, 100_000m, 70_000m, 10_000m, "LR1", Today, Now).Value;
        loan.RecordRepayment(10_001m, Today, null, Now).Error!.Code.ShouldBe(LifeErrorCodes.RepaymentExceedsBalance);
        loan.RecordRepayment(4_000m, Today, null, Now).IsSuccess.ShouldBeTrue();
        loan.RecordRepayment(6_000m, Today, null, Now).IsSuccess.ShouldBeTrue();
        (loan.Status, loan.Balance, loan.Transactions.Sum(t => t.TxnType == LoanTransaction.Advance ? t.Amount : -t.Amount)).ShouldBe((LoanStatus.Repaid, 0m, 0m));
    }

    [Fact]
    public void GroupMembers_CoverIsAMultipleOfSalary_AndAboveTheFreeCoverLimitNeedsEvidence()
    {
        var scheme = GroupScheme.SetUpScheme(Guid.CreateVersion7(), "GS1", Guid.CreateVersion7(), "LIF-GRP", 1, "EMPLOYER", Today, Today.AddYears(1), "ZAR", Today).Value;
        var manager = new MemberCategory("MGMT", 4m);
        var low = scheme.EnrolMember(Guid.CreateVersion7(), manager, 500_000m, Today, 2_500_000m, Now).Value;
        var high = scheme.EnrolMember(Guid.CreateVersion7(), manager, 900_000m, Today, 2_500_000m, Now).Value;

        (low.CoverAmount, low.EvidenceStatus).ShouldBe((2_000_000m, EvidenceStatus.NotRequired));
        (high.CoverAmount, high.EvidenceStatus).ShouldBe((3_600_000m, EvidenceStatus.Required));
        scheme.EnrolMember(high.PartyId, manager, 1m, Today, null, Now).Error!.Code.ShouldBe(ErrorCodes.DuplicateRecord);
        scheme.AcceptEvidence(high.Id).IsSuccess.ShouldBeTrue();
        scheme.TerminateScheme(Today.AddMonths(6)).IsSuccess.ShouldBeTrue();
        scheme.Members.ShouldAllBe(m => m.LeftOn == Today.AddMonths(6));
    }

    // Parts blocked by OQ-051 and OQ-052: the expected behaviour is recorded, and the tests are skipped until the decisions are made.

    [Fact(Skip = "OQ-051: how acceptance and referral rule sets decide an underwriting outcome is pending underwriting's decision.")]
    public void AutomaticUnderwriting_AcceptsReferOrDeclinesFromTheRuleSet_OQ051()
    {
    }

    [Fact(Skip = "OQ-052: rating loadings (percent and per mille) into the premium is pending actuarial decisions.")]
    public void IssuePolicy_WithALoading_RatesTheLoadedPremium_OQ052()
    {
    }

    [Fact(Skip = "OQ-052: surrender value scales and their calculation are pending actuarial decisions.")]
    public void SurrenderPolicy_PaysTheSurrenderValueLessLoans_OQ052()
    {
    }

    [Fact(Skip = "OQ-052: the paid-up value calculation is pending actuarial decisions.")]
    public void MakePaidUp_ReducesCoverToThePaidUpValue_OQ052()
    {
    }

    [Fact(Skip = "OQ-052: the maturity amount calculation is pending actuarial decisions.")]
    public void MaturityCase_CalculatesTheAmountFromTheProductVersion_OQ052()
    {
    }

    [Fact(Skip = "OQ-052: loan interest (day count and capitalisation of life.cfg_policy_loan_rates) is pending actuarial decisions.")]
    public void AccrueInterest_CapitalisesAtTheLoanRate_OQ052()
    {
    }

    [Fact(Skip = "OQ-052: cash value valuation is pending actuarial decisions.")]
    public void CashValues_AreValuedFromTheSurrenderValueScale_OQ052()
    {
    }

    [Fact(Skip = "OQ-052: group scheme premium rating is pending actuarial decisions.")]
    public void GroupScheme_IsRatedOnItsMembersCover_OQ052()
    {
    }
}
