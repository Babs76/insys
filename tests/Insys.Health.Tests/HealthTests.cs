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
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Domain;
using Insys.Health.Domain;
using Insys.Health.Domain.Adjudication;
using Insys.Health.Domain.Benefits;
using Insys.Health.Domain.Care;
using Insys.Health.Domain.Members;
using Insys.Health.Domain.Payments;
using Insys.Testing;

using Application = Insys.Health.Application;

namespace Insys.Health.Tests;

public sealed class HealthTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 22);
    private static readonly Guid Principal = Guid.CreateVersion7();

    private static string Spec(string aggregate, params (string From, string To)[] replacements) =>
        string.Join("|", DomainModelSpec.Transitions("health", aggregate).Select(t => replacements.Aggregate(t, (s, r) => s.Replace(r.From, r.To, StringComparison.Ordinal)))
            .OrderBy(t => t, StringComparer.Ordinal));

    private static string Model<TState, TTrigger>(StateMachine<TState, TTrigger> machine) where TState : struct, Enum where TTrigger : struct, Enum =>
        string.Join("|", machine.Transitions.Select(t => $"{t.From}-{t.Trigger}->{t.To}").OrderBy(t => t, StringComparer.Ordinal));

    private static HealthMembership Membership(DateOnly? start = null, IReadOnlyList<WaitingPeriod>? waiting = null) =>
        HealthMembership.EnrolMember(Guid.CreateVersion7(), "HM0001", Principal, null, null, new PlanOption("HLT-CORE", "A", 1), start ?? Today.AddMonths(-6), waiting ?? [],
            new ContributionBilling(Principal, Guid.CreateVersion7(), "DEBIT_ORDER", new ContributionRate(1_850m, 0m, null)), "ZAR", Today, Now).Value;

    [Fact]
    public void Lifecycles_MatchDomainModel()
    {
        Model(HealthScheme.Lifecycle).ShouldBe(Spec("HealthScheme", ("Start date reached", "StartDateReached")));
        Model(HealthMembership.Lifecycle).ShouldBe(Spec("HealthMembership", ("Start date reached", "StartDateReached")));
        Model(Preauthorisation.Lifecycle).ShouldBe(Spec("Preauthorisation", ("Matched to a claim", "MatchedToClaim"), ("Validity ends", "ValidityEnds"),
            ("Conditions met", "ConditionsMet")));
        // OQ-103 (Wave 2 cases) adds "Rejected-ReopenClaim->Validated" for an upheld dispute or appeal, not yet in domain-model.json.
        Model(MedicalClaim.Lifecycle).ShouldBe(string.Join("|", Spec("MedicalClaim", ("Information received", "InformationReceived"), ("Payment run released", "PaymentRunReleased"))
            .Split('|').Append("Rejected-ReopenClaim->Validated").OrderBy(t => t, StringComparer.Ordinal)));
        Model(UtilisationCase.Lifecycle).ShouldBe(Spec("UtilisationCase"));
        Model(HealthFraudCase.Lifecycle).ShouldBe(Spec("HealthFraudCase"));
        // D-2 (Wave 2 batch 1, V0056) adds "Calculated-RejectRun->Rejected", not yet in domain-model.json.
        Model(ProviderPaymentRun.Lifecycle).ShouldBe(string.Join("|", Spec("ProviderPaymentRun", ("Payments matched", "PaymentsMatched")).Split('|')
            .Append("Calculated-RejectRun->Rejected").OrderBy(t => t, StringComparer.Ordinal)));
    }

    [Fact]
    public void Dependants_StayWithinTheirTypesAgeLimit_WithTheStudentExtension_HLT002()
    {
        var membership = Membership();
        var child = new DependantRule("CHILD", 21, 26);
        var rate = new ContributionRate(1_850m, 0m, null);
        membership.AddDependant(Guid.CreateVersion7(), child, Today.AddYears(-23), false, Today, rate, Now).Error!.Code.ShouldBe(HealthErrorCodes.DependantNotEligible);
        var student = membership.AddDependant(Guid.CreateVersion7(), child, Today.AddYears(-23), true, Today, rate, Now).Value;
        var second = membership.AddDependant(Guid.CreateVersion7(), child, Today.AddYears(-5), false, Today, rate, Now).Value;

        (student.Suffix, second.Suffix).ShouldBe(("01", "02"));
        membership.DomainEvents.OfType<Domain.Members.DependantAdded>().Last().MonthlyContribution.ShouldBe(1_850m * 3);
        membership.Covers(student.PartyId, Today).ShouldBeTrue();
        membership.PartyFor("02").ShouldBe(second.PartyId);
    }

    [Fact]
    public void SchemeContributions_AreSplitByTheEmployerShare_TheEmployersPartBilledToTheSponsor()
    {
        var sponsor = Guid.CreateVersion7();
        var rate = new ContributionRate(1_850.55m, 33.33m, sponsor);
        (rate.EmployerPerMember, rate.MemberPerMember).ShouldBe((616.79m, 1_233.76m));

        var membership = HealthMembership.EnrolMember(Guid.CreateVersion7(), "HM0002", Principal, Guid.CreateVersion7(), "ALL_STAFF", new PlanOption("HLT-CORE", "A", 1),
            Today, [], new ContributionBilling(Principal, Guid.CreateVersion7(), "DEBIT_ORDER", rate), "ZAR", Today, Now).Value;
        var enrolled = membership.DomainEvents.OfType<Domain.Members.HealthMemberEnrolled>().Single();
        (enrolled.MonthlyContribution, enrolled.EmployerContribution, enrolled.SponsorPartyId).ShouldBe((1_233.76m, 616.79m, (Guid?)sponsor));
        membership.AddDependant(Guid.CreateVersion7(), new DependantRule("SPOUSE", 120, null), Today.AddYears(-40), false, Today, rate, Now).IsSuccess.ShouldBeTrue();
        var added = membership.DomainEvents.OfType<Domain.Members.DependantAdded>().Single();
        (added.MonthlyContribution, added.EmployerContribution).ShouldBe((2_467.52m, 1_233.58m));
    }

    [Fact]
    public void Membership_CoversOnlyWhileActive_AndWithinItsDates()
    {
        var membership = Membership();
        membership.Covers(Principal, Today).ShouldBeTrue();
        membership.SuspendMembership(Today, "ARREARS", Now).IsSuccess.ShouldBeTrue();
        membership.Covers(Principal, Today).ShouldBeFalse();
        membership.ReinstateMembership(Today, "PAID", Now).IsSuccess.ShouldBeTrue();
        membership.TerminateMembership(Today.AddDays(5), "RESIGNED", Now).IsSuccess.ShouldBeTrue();
        membership.Covers(Principal, Today).ShouldBeFalse();
        membership.Events.Select(e => e.EventType).ShouldBe(["ENROL", "SUSPEND", "REINSTATE", "TERMINATE"]);
    }

    [Fact]
    public void Accumulator_UsedPlusReservedNeverExceedsTheLimit_HLT007()
    {
        var key = new AccumulatorKey(AccumulatorScope.Member, Principal, "SPECIALIST", new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        var accumulator = BenefitAccumulator.OpenNewPeriod(Guid.CreateVersion7(), Guid.CreateVersion7(), key, 1_000m);

        accumulator.ReserveBenefit(600m, Now).IsSuccess.ShouldBeTrue();
        accumulator.ReserveBenefit(500m, Now).Error!.Code.ShouldBe(HealthErrorCodes.LimitExceeded);
        accumulator.ConsumeBenefit(new AccumulatorChange(key, 1_000m, 900m, 600m, 0, 0), Now).IsSuccess.ShouldBeTrue();
        (accumulator.UsedAmount, accumulator.ReservedAmount, accumulator.Remaining).ShouldBe((900m, 0m, 100m));
        accumulator.ConsumeBenefit(new AccumulatorChange(key, 1_000m, 101m, 0, 0, 0), Now).Error!.Code.ShouldBe(HealthErrorCodes.LimitExceeded);
        accumulator.ConsumeBenefit(new AccumulatorChange(key, 1_000m, 100m, 0, 0, 0), Now).IsSuccess.ShouldBeTrue();
        accumulator.DomainEvents.OfType<Domain.Benefits.BenefitLimitReached>().ShouldHaveSingleItem();
    }

    [Fact]
    public void Preauthorisation_ReservesWhenApproved_AndReleasesWhatItHoldsWhenItExpires_HLT005()
    {
        var preauth = Preauthorisation.RequestAuthorisation(Guid.CreateVersion7(), "PA1", Guid.CreateVersion7(), Principal, "PR-1", ["K35"], "PROCEDURE",
            [new PreauthLineRequest("0300", 1, 1_200m)]).Value;
        var line = preauth.Lines.Single();
        preauth.Approve([new LineAuthorisation(line.Id, 1_000m, "SPECIALIST")], Today, 30, null, Principal, Now).IsFailure.ShouldBeTrue();
        preauth.ReviewClinically(Principal).IsSuccess.ShouldBeTrue();
        preauth.Approve([new LineAuthorisation(line.Id, 1_300m, "SPECIALIST")], Today, 30, null, Principal, Now).IsFailure.ShouldBeTrue();
        preauth.Approve([new LineAuthorisation(line.Id, 1_000m, "SPECIALIST")], Today, 30, null, Principal, Now).IsSuccess.ShouldBeTrue();

        preauth.ValidTo.ShouldBe(Today.AddDays(29));
        preauth.DomainEvents.OfType<Domain.Care.PreauthorisationApproved>().Single().Reservations.Single().Amount.ShouldBe(1_000m);
        preauth.Expire(Today.AddDays(29), Now).IsFailure.ShouldBeTrue();
        preauth.Expire(Today.AddDays(30), Now).IsSuccess.ShouldBeTrue();
        preauth.DomainEvents.OfType<Domain.Care.PreauthorisationReservationsChanged>().Single().Changes.Single().Amount.ShouldBe(-1_000m);
        preauth.DomainEvents.OfType<Domain.Care.PreauthorisationExpired>().ShouldHaveSingleItem();
    }

    [Fact]
    public void AConditionalApproval_ReservesAndCanBeUsed_OrBecomesApprovedWhenItsConditionsAreMet()
    {
        Preauthorisation Conditional()
        {
            var preauth = Preauthorisation.RequestAuthorisation(Guid.CreateVersion7(), "PA2", Guid.CreateVersion7(), Principal, "PR-1", [], "PROCEDURE",
                [new PreauthLineRequest("0300", 1, 1_000m)]).Value;
            preauth.ReviewClinically(Principal);
            preauth.Approve([new LineAuthorisation(preauth.Lines[0].Id, 800m, "SPECIALIST")], Today, 30, "Pre-operative tests first", Principal, Now).IsSuccess.ShouldBeTrue();
            return preauth;
        }

        var used = Conditional();
        (used.Status, used.Lines[0].ReservedAmount).ShouldBe((PreauthStatus.ConditionallyApproved, 800m));
        used.DomainEvents.OfType<Domain.Care.PreauthorisationApproved>().ShouldHaveSingleItem();
        used.ValidOn(Today).ShouldBeTrue();
        used.RecordUse(used.Lines[0].Id, 800m, 800m, Now).IsSuccess.ShouldBeTrue();
        used.Status.ShouldBe(PreauthStatus.Utilised);

        var met = Conditional();
        met.ConfirmConditionsMet(Principal).IsSuccess.ShouldBeTrue();
        met.Status.ShouldBe(PreauthStatus.Approved);

        var expiring = Conditional();
        expiring.Expire(Today.AddDays(30), Now).IsSuccess.ShouldBeTrue();
        expiring.Status.ShouldBe(PreauthStatus.Expired);
        Conditional().CancelAuthorisation(Now).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void AUsedPreauthorisation_ReleasesTheRestOfItsReservation()
    {
        var preauth = Preauthorisation.RequestAuthorisation(Guid.CreateVersion7(), "PA1", Guid.CreateVersion7(), Principal, "PR-1", [], "PROCEDURE",
            [new PreauthLineRequest("0300", 1, 1_000m)]).Value;
        var line = preauth.Lines.Single();
        preauth.ReviewClinically(Principal);
        preauth.Approve([new LineAuthorisation(line.Id, 1_000m, "SPECIALIST")], Today, 30, null, Principal, Now);

        preauth.RecordUse(line.Id, 800m, 720m, Now).IsSuccess.ShouldBeTrue();
        (preauth.Status, line.ConsumedAmount, line.ReservedAmount).ShouldBe((PreauthStatus.Utilised, 720m, 0m));
        preauth.DomainEvents.OfType<Domain.Care.PreauthorisationReservationsChanged>().Single().Changes.Single().Amount.ShouldBe(-200m);
    }

    [Fact]
    public void Claim_ValidationFailure_RejectsIt_AndAPendedLinePendsTheWholeClaim_HLT007()
    {
        var rejected = MedicalClaim.SubmitClaim(Guid.CreateVersion7(), "MC1", ClaimSources.Provider, "PR-1", Guid.CreateVersion7(), Principal, Today, "API", "ZAR", null,
            [new ClaimLineRequest("0300", null, 1, 900m, null)], Now).Value;
        rejected.ValidateClaim("NOT_COVERED", Now).IsSuccess.ShouldBeTrue();
        (rejected.Status, rejected.PendReason).ShouldBe((MedicalClaimStatus.Rejected, "NOT_COVERED"));

        var claim = MedicalClaim.SubmitClaim(Guid.CreateVersion7(), "MC2", ClaimSources.Provider, "PR-1", Guid.CreateVersion7(), Principal, Today, "API", "ZAR", null,
            [new ClaimLineRequest("0300", null, 1, 900m, null), new ClaimLineRequest("0190", null, 1, 400m, null)], Now).Value;
        claim.ValidateClaim(null, Now);
        var pended = new AdjudicationResult([
            new LineResult(1, LineOutcome.Pay, null, "SPECIALIST", NetworkStatus.InNetwork, 900m, 900m, 0, 0, 900m, 0, null, null),
            new LineResult(2, LineOutcome.Pend, AdjudicationReasons.Duplicate, "GP", NetworkStatus.InNetwork, 400m, 400m, 0, 0, 0, 400m, null, null),
        ], [], []);
        claim.AdjudicateClaim(pended, Now).IsSuccess.ShouldBeTrue();
        (claim.Status, claim.TotalPaid, claim.PendReason).ShouldBe((MedicalClaimStatus.Pended, (decimal?)null, AdjudicationReasons.Duplicate));
        claim.ApproveForPayment(Principal, Now).IsFailure.ShouldBeTrue();

        var resolved = pended with { Lines = [pended.Lines[0], pended.Lines[1] with { Outcome = LineOutcome.Reject, Reason = AdjudicationReasons.Reviewer }] };
        claim.AdjudicateClaim(resolved, Now).IsSuccess.ShouldBeTrue();
        (claim.Status, claim.TotalPaid).ShouldBe((MedicalClaimStatus.Adjudicated, 900m));

        // OQ-102: paying 900 of the 1 300 claimed is a partial approval, which gives a PARTIAL decision reason.
        claim.ApproveForPayment(Principal, Now).Error!.Code.ShouldBe(HealthErrorCodes.RationaleRequired);
        claim.ApproveForPayment(Principal, Now, "LINE_DECLINED", "DECLINE").IsFailure.ShouldBeTrue();
        claim.ApproveForPayment(Principal, Now, "LINE_DECLINED", MedicalClaimOutcomes.PartialReasonType).IsSuccess.ShouldBeTrue();
        (claim.DecisionOutcome, claim.DecisionReasonCode).ShouldBe((MedicalClaimOutcomes.PartlyApproved, "LINE_DECLINED"));
        claim.NoticeDecisionRights(Principal, "HLT-CORE", null, Today, Now).IsSuccess.ShouldBeTrue();
        claim.DomainEvents.OfType<MedicalClaimDecisionRightsDue>().Single().TotalPaid.ShouldBe(900m);
    }

    /// <summary>F013, F032, OQ-103: an upheld appeal sends a rejected medical claim back to Validated, to be adjudicated again; any other claim is left as it is.</summary>
    [Fact]
    public void UpheldAppeal_SendsARejectedClaimBackToValidation_OQ103()
    {
        var claim = MedicalClaim.SubmitClaim(Guid.CreateVersion7(), "MC-R", ClaimSources.Provider, "PR-1", Guid.CreateVersion7(), Principal, Today, "API", "ZAR", null,
            [new ClaimLineRequest("0300", null, 1, 900m, null)], Now).Value;
        claim.ReopenForCase("CASE-1").ShouldBeFalse("a received claim is not rejected");
        claim.ValidateClaim("NOT_COVERED", Now).IsSuccess.ShouldBeTrue();
        claim.Status.ShouldBe(MedicalClaimStatus.Rejected);

        claim.ReopenForCase("CASE-1").ShouldBeTrue();
        (claim.Status, claim.TotalPaid, claim.PendReason).ShouldBe((MedicalClaimStatus.Validated, (decimal?)null, (string?)null));
        claim.ReopenForCase("CASE-1").ShouldBeFalse("a redelivery changes nothing");
        claim.AdjudicateClaim(new AdjudicationResult([new LineResult(1, LineOutcome.Pay, null, "SPECIALIST", NetworkStatus.InNetwork, 900m, 900m, 0, 0, 900m, 0, null, null)],
            [], []), Now).IsSuccess.ShouldBeTrue();
        claim.ApproveForPayment(Principal, Now).IsSuccess.ShouldBeTrue("paid in full: approved, no reason needed");
        claim.DecisionOutcome.ShouldBe(MedicalClaimOutcomes.Approved);
    }

    /// <summary>
    /// F103: a payment run is seen by its remittances whose claims are all in the caller's scope (by their memberships), so a
    /// remittance mixing branches is left out; acting on the run needs every claim in scope.
    /// </summary>
    [Fact]
    public void PaymentRun_ShowsOnlyRemittancesWhoseClaimsAreAllInScope_F103()
    {
        Guid a = Guid.CreateVersion7(), b = Guid.CreateVersion7();
        var memberships = new Dictionary<Guid, Application.MembershipScope>
        {
            [a] = new("HM-A", "HLT-PROD", "BR-A"),
            [b] = new("HM-B", "HLT-PROD", "BR-B"),
        };
        MedicalClaim Claim(string source, string? provider, Guid membership) =>
            MedicalClaim.SubmitClaim(Guid.CreateVersion7(), "MC", source, provider, membership, Principal, Today, "API", "ZAR", null, [new ClaimLineRequest("0300", null, 1, 100m, null)], Now).Value;
        var claims = new[]
        {
            Claim(ClaimSources.Provider, "PR-1", a), Claim(ClaimSources.Provider, "PR-2", b), Claim(ClaimSources.Provider, "PR-3", a), Claim(ClaimSources.Provider, "PR-3", b),
            Claim(ClaimSources.Member, null, a),
        };
        var run = ProviderPaymentRun.OpenRun(Guid.CreateVersion7(), "PRV-WEEKLY", "ZAR", Now);
        run.CutOffRun(Now);
        run.CalculateRun([.. new[] { "PR-1", "PR-2", "PR-3", "HM-A" }.Select(p => new RemittanceRequest(p, Guid.CreateVersion7(), Guid.CreateVersion7(), 100m, 1))], Principal, Now);
        var branchA = new AccessScope([new PermissionGrant(null, new HashSet<string> { "BR-A" })]);

        var (visible, partly) = Application.RunVisibility.Of(run.Remittances, claims, memberships, branchA);

        visible.Select(r => r.ProviderId).ShouldBe(["PR-1", "HM-A"], "PR-3 pays a branch-B claim too");
        partly.ShouldBeTrue();
        Application.RunVisibility.CoversAll(claims, memberships, branchA).ShouldBeFalse();
        Application.RunVisibility.CoversAll(claims.Where(c => c.MembershipId == a), memberships, branchA).ShouldBeTrue();
        Application.RunVisibility.Of(run.Remittances, claims, memberships, AccessScope.Unrestricted).Partly.ShouldBeFalse();
    }

    [Fact]
    public void PaymentRun_TotalIsTheSumOfRemittances_AndTheApprovalIsBoundToIt_HLT009()
    {
        var run = ProviderPaymentRun.OpenRun(Guid.CreateVersion7(), "PRV-WEEKLY", "ZAR", Now);
        run.CalculateRun([], Principal, Now).IsFailure.ShouldBeTrue();
        run.CutOffRun(Now);
        run.CalculateRun([], Principal, Now).Error!.Code.ShouldBe(HealthErrorCodes.RunEmpty);
        run.CalculateRun([new RemittanceRequest("PR-1", Guid.CreateVersion7(), Guid.CreateVersion7(), 900m, 1), new RemittanceRequest("PR-2", Guid.CreateVersion7(),
            Guid.CreateVersion7(), 450.50m, 2)], Principal, Now).IsSuccess.ShouldBeTrue();
        (run.TotalAmount, run.ClaimCount).ShouldBe((1_350.50m, 3));

        run.DecideApproval(true, Guid.CreateVersion7(), Guid.CreateVersion7(), "stale").Error!.Code.ShouldBe(HealthErrorCodes.ApprovalSubjectChanged);
        run.DecideApproval(true, Guid.CreateVersion7(), Guid.CreateVersion7(), run.SubjectHash).IsSuccess.ShouldBeTrue();
        run.ReleaseRun(Today, "TPL-RA", Now).IsSuccess.ShouldBeTrue();
        run.RecordPayment(run.Remittances[0].Id, Guid.CreateVersion7(), Today);
        run.Status.ShouldBe(RunStatus.Released);
        run.RecordPayment(run.Remittances[1].Id, Guid.CreateVersion7(), Today);
        run.Status.ShouldBe(RunStatus.Reconciled);
    }

    [Fact]
    public void Cases_AreConcludedOnlyByTheirReviewerOrInvestigator()
    {
        var reviewer = Guid.CreateVersion7();
        var utilisation = UtilisationCase.OpenCase(Guid.CreateVersion7(), Guid.CreateVersion7(), "HC-SPEC", null, Now);
        utilisation.ReviewCase(reviewer);
        utilisation.ConcludeCase("APPROPRIATE", Guid.CreateVersion7(), Now).Error!.Code.ShouldBe(ErrorCodes.Forbidden);
        utilisation.ConcludeCase("APPROPRIATE", reviewer, Now).IsSuccess.ShouldBeTrue();

        var fraud = HealthFraudCase.OpenCase(Guid.CreateVersion7(), "CLAIM", "MC1", ["FR-HIGH"], Now);
        fraud.ConcludeCase("NONE", reviewer, Now).IsFailure.ShouldBeTrue();
        fraud.Investigate(reviewer);
        fraud.ConcludeCase("NO_FRAUD", reviewer, Now).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void IsoDurations_AreAddedToDates()
    {
        IsoDuration.Add(Today, "P6M").ShouldBe(Today.AddMonths(6));
        IsoDuration.Add(Today, "P1Y2M10D").ShouldBe(Today.AddYears(1).AddMonths(2).AddDays(10));
        IsoDuration.Add(Today, "P2W").ShouldBe(Today.AddDays(14));
        IsoDuration.Add(Today, "6 months").ShouldBeNull();
    }

    private static MedicalClaim ApprovedForPayment()
    {
        var claim = MedicalClaim.SubmitClaim(Guid.CreateVersion7(), "MC3", ClaimSources.Provider, "PR-1", Guid.CreateVersion7(), Principal, Today, "API", "ZAR", null,
            [new ClaimLineRequest("0300", null, 1, 900m, null)], Now).Value;
        claim.ValidateClaim(null, Now);
        claim.AdjudicateClaim(new AdjudicationResult([new LineResult(1, LineOutcome.Pay, null, "SPECIALIST", NetworkStatus.InNetwork, 900m, 900m, 0, 0, 900m, 0, null, null)],
            [], []), Now).IsSuccess.ShouldBeTrue();
        claim.ApproveForPayment(Principal, Now).IsSuccess.ShouldBeTrue();
        return claim;
    }

    private static ProviderPaymentRun CalculatedRun()
    {
        var run = ProviderPaymentRun.OpenRun(Guid.CreateVersion7(), "PRV-WEEKLY", "ZAR", Now);
        run.CutOffRun(Now);
        run.CalculateRun([new RemittanceRequest("PR-1", Guid.CreateVersion7(), Guid.CreateVersion7(), 900m, 1)], Principal, Now).IsSuccess.ShouldBeTrue();
        return run;
    }

    [Fact]
    public void ARunApprovedByAnApproval_IsGrantedByIt_AndAChangedOneIsNot_D5()
    {
        var run = CalculatedRun();
        var approvalId = Guid.CreateVersion7();

        run.DecideApproval(true, approvalId, Guid.CreateVersion7(), "the run before a change").Error!.Code.ShouldBe(HealthErrorCodes.ApprovalSubjectChanged);
        run.GrantedBy(approvalId).ShouldBeFalse();
        run.DecideApproval(true, approvalId, Guid.CreateVersion7(), run.SubjectHash).IsSuccess.ShouldBeTrue();
        (run.GrantedBy(approvalId), run.GrantedBy(Guid.CreateVersion7())).ShouldBe((true, false));
    }

    [Fact]
    public void RejectedPaymentRun_EndsRejected_KeepsItsRemittances_AndReleasesItsClaimsForANewRun_D2()
    {
        var claim = ApprovedForPayment();
        var run = CalculatedRun();
        claim.AssignToRun(run.Id).IsSuccess.ShouldBeTrue();
        var approvalId = Guid.CreateVersion7();

        // Expired or withdrawn: no checker.
        run.DecideApproval(false, approvalId, Guid.Empty, run.SubjectHash).IsSuccess.ShouldBeTrue();
        (run.Status, run.ApprovalId, run.ApprovedBy, run.EndedBy(approvalId)).ShouldBe((RunStatus.Rejected, (Guid?)approvalId, (Guid?)null, true));
        run.Remittances.ShouldHaveSingleItem().PaymentId.ShouldBeNull();
        run.ReleaseRun(Today, "TPL-RA", Now).IsFailure.ShouldBeTrue();

        claim.LeaveRun(run.Id).ShouldBeTrue();
        claim.PaymentRunId.ShouldBeNull();
        var next = CalculatedRun();
        claim.AssignToRun(next.Id).IsSuccess.ShouldBeTrue();
        claim.LeaveRun(run.Id).ShouldBeFalse();
        claim.PaymentRunId.ShouldBe(next.Id);

        // Applied again (a redelivery), it changes nothing.
        run.DecideApproval(false, approvalId, Guid.Empty, run.SubjectHash).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void PaymentRunEnding_ForAnotherSubjectOrAMovedOnRun_IsRefused_D5()
    {
        var run = CalculatedRun();
        run.DecideApproval(false, Guid.CreateVersion7(), Guid.CreateVersion7(), "stale").Error!.Code.ShouldBe(HealthErrorCodes.ApprovalSubjectChanged);
        run.Status.ShouldBe(RunStatus.Calculated);

        run.DecideApproval(true, Guid.CreateVersion7(), Guid.CreateVersion7(), run.SubjectHash).IsSuccess.ShouldBeTrue();
        run.DecideApproval(false, Guid.CreateVersion7(), Guid.CreateVersion7(), run.SubjectHash).Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
        run.Status.ShouldBe(RunStatus.Approved);
    }
}
