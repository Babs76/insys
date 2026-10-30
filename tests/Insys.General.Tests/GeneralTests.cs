/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : General.Tests
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
    private static readonly DateOnly End = new(2027, 9, 30);

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
        Model(GeneralQuote.Lifecycle).ShouldBe(Spec("GeneralQuote", ("Referral approved", "ReferralApproved"), ("Referral declined", "ReferralDeclined"),
            ("Validity ends", "ValidityEnds")));
        Model(GeneralPolicy.Lifecycle).ShouldBe(Spec("GeneralPolicy", ("Period starts", "PeriodStarts"), ("ReinstatePolicy (where permitted)", "ReinstatePolicy"),
            ("Period ends without renewal", "PeriodEndsWithoutRenewal")));
        Model(GeneralEndorsement.Lifecycle).ShouldBe(Spec("GeneralEndorsement", ("Policy updated", "PolicyUpdated")));
        // OQ-103 (Wave 2 cases) adds "Declined-ReopenClaim->Reopened" for an upheld dispute, not yet in domain-model.json.
        Model(GeneralClaim.Lifecycle).ShouldBe(string.Join("|", Spec("GeneralClaim", ("Payment completed", "PaymentCompleted"), ("Assessment resumes", "AssessmentResumes"))
            .Split('|').Append("Declined-ReopenClaim->Reopened")
            // F006 (Wave 4, Ade, 29 October 2026): grace ended unpaid declines a claim for a loss from the cancellation date, short of payment.
            .Concat(new[] { "FirstNotification", "Registered", "UnderAssessment", "Approved" }.Select(from => $"{from}-DeclineForNonPayment->Declined"))
            .OrderBy(t => t, StringComparer.Ordinal)));
        Model(LossAssessment.Lifecycle).ShouldBe(Spec("LossAssessment", ("Assessor starts", "AssessorStarts"), ("Further work requested", "FurtherWorkRequested")));
        Model(RecoveryCase.Lifecycle).ShouldBe(Spec("RecoveryCase", ("Demand sent", "DemandSent")));
    }

    /// <summary>
    /// BR-095: a quote priced as it is made asks for its price once, and its later versions are priced the same way; a
    /// rating issue keeps it Draft until it is rated; a rules referral has no referrer, so any underwriter decides it.
    /// </summary>
    [Fact]
    public void AQuotePricedAsItIsMade_AsksOnce_KeepsItsIssueWhileDraft_AndItsRulesReferralHasNoReferrer_BR095()
    {
        var quote = Quote();
        quote.RequestPricing("""{"sum_insured":200000}""", Today, Now).IsSuccess.ShouldBeTrue();
        quote.PricedOnCreation.ShouldBeTrue();
        var requested = quote.DomainEvents.OfType<GeneralQuotePricingRequested>().Single();
        (requested.QuoteId, requested.ProductVersion, requested.RatingDate, requested.ValuesJson).ShouldBe((quote.Id, 1, Today, """{"sum_insured":200000}"""));

        var next = GeneralQuote.CreateQuote(Guid.CreateVersion7(), "GQ-1", quote, Motor, quote.ProposerPartyId, null, "ZAR", Start, End, [Risk()], Today).Value;
        (next.QuoteVersion, next.PricedOnCreation).ShouldBe((2, true));

        quote.RecordRatingIssue("RATING_INPUT_MISSING", "Input vehicle_group (factor VEHICLE_GROUP) is missing.").IsSuccess.ShouldBeTrue();
        (quote.Status, quote.RatingIssueCode).ShouldBe((QuoteStatus.Draft, "RATING_INPUT_MISSING"));
        quote.RateQuote(Guid.CreateVersion7(), "MOTOR", 1, 3, 6_050m, "ZAR").IsSuccess.ShouldBeTrue();
        (quote.Status, quote.RatingIssueCode, quote.RatingIssue).ShouldBe((QuoteStatus.Rated, null, null));
        quote.RecordRatingIssue("RATING_INPUT_MISSING", "late").Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
        quote.RequestPricing("{}", Today, Now).Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition);

        quote.ReferQuoteByRules("AGG_LIMIT", Now).IsSuccess.ShouldBeTrue();
        quote.Referrals.Single().ReferredBy.ShouldBeNull();
        var underwriter = Guid.CreateVersion7();
        var referred = quote.DomainEvents.OfType<GeneralQuoteReferred>().Single();
        (referred.ReasonCode, referred.SumInsured, referred.ProductCode).ShouldBe(("AGG_LIMIT", 200_000m, "MOTOR"), "the referral task is routed on the product and sum insured");
        quote.DecideReferral(true, "ACCEPT", [], null, underwriter, Now).IsSuccess.ShouldBeTrue();
        (quote.Status, quote.Premium).ShouldBe((QuoteStatus.Rated, 6_050m));
        quote.DomainEvents.OfType<GeneralQuoteReferralDecided>().Single().Approved.ShouldBeTrue();
    }

    /// <summary>BR-092: a risk object is registered for its owner, and staff change or clear the owner; the same owner again is refused.</summary>
    [Fact]
    public void ARiskObjectsOwner_IsSetOnRegistration_AndChangedOnlyToAnother_BR092()
    {
        var schema = AttributeSchema.Parse("VEHICLE", 1, ["registration:String(10):Y"], []).Value;
        var values = new Dictionary<string, object?> { ["registration"] = "CA1" };
        var (client, other) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var risk = RiskObject.RegisterRiskObject(Guid.CreateVersion7(), schema, values, "{}", null, null, new(null, null, null), Today, Now, owner: client).Value;
        risk.OwnerPartyId.ShouldBe(client);

        risk.ChangeOwner(client).Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
        risk.ChangeOwner(other).IsSuccess.ShouldBeTrue();
        risk.OwnerPartyId.ShouldBe(other);
        risk.ChangeOwner(null).IsSuccess.ShouldBeTrue();
        risk.OwnerPartyId.ShouldBeNull();
    }

    [Fact]
    public void AttributeSchema_ValidatesTypesMandatoryFieldsAndRules_GEN005()
    {
        var schema = AttributeSchema.Parse("VEHICLE", 2, ["registration:String(10):Y", "year:Integer:Y", "tracked:Boolean:N"], ["year <= current_year"]).Value;

        schema.Validate(new Dictionary<string, object?> { ["registration"] = "CA123456", ["year"] = 2024m }, Today).IsSuccess.ShouldBeTrue();
        schema.Validate(new Dictionary<string, object?> { ["year"] = 2024m }, Today).Error!.Message.ShouldContain("registration is required");
        schema.Validate(new Dictionary<string, object?> { ["registration"] = "CA12345678901", ["year"] = 2024m }, Today).IsFailure.ShouldBeTrue();
        schema.Validate(new Dictionary<string, object?> { ["registration"] = "CA1", ["year"] = 2024.5m }, Today).IsFailure.ShouldBeTrue();
        schema.Validate(new Dictionary<string, object?> { ["registration"] = "CA1", ["year"] = 2027m }, Today).IsFailure.ShouldBeTrue();

        // The catalogue's sample rule, with arithmetic (ADR-008).
        var sample = AttributeSchema.Parse("VEHICLE", 3, ["year:Integer:Y"], ["year <= current_year + 1"]).Value;
        sample.Validate(new Dictionary<string, object?> { ["year"] = 2027m }, Today).IsSuccess.ShouldBeTrue();
        sample.Validate(new Dictionary<string, object?> { ["year"] = 2028m }, Today).IsFailure.ShouldBeTrue();
        schema.Validate(new Dictionary<string, object?> { ["registration"] = "CA1", ["year"] = 2024m, ["colour"] = "red" }, Today).Error!.Message.ShouldContain("colour");
    }

    [Fact]
    public void RiskObject_HasAValuationBasisWheneverValued()
    {
        var schema = AttributeSchema.Parse("VEHICLE", 1, ["registration:String(10):Y"], []).Value;
        var values = new Dictionary<string, object?> { ["registration"] = "CA1" };
        RiskObject.RegisterRiskObject(Guid.CreateVersion7(), schema, values, "{}", 150_000m, null, new(null, null, null), Today, Now).Error!.Code
            .ShouldBe(GeneralErrorCodes.ValuationBasisRequired);

        var risk = RiskObject.RegisterRiskObject(Guid.CreateVersion7(), schema, values, "{}", 150_000m, "MARKET", new(null, null, "Z1"), Today, Now, "ZAR").Value;
        risk.AttributeSchemaVersion.ShouldBe(1);
        risk.RevalueRiskObject(10m, " ", "ZAR").IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void RiskObject_ValuationNamesItsCurrency_BR033()
    {
        var schema = AttributeSchema.Parse("VEHICLE", 1, ["registration:String(10):Y"], []).Value;
        var values = new Dictionary<string, object?> { ["registration"] = "CA1" };
        RiskObject.RegisterRiskObject(Guid.CreateVersion7(), schema, values, "{}", 150_000m, "MARKET", new(null, null, null), Today, Now).Error!.Code
            .ShouldBe(GeneralErrorCodes.ValuationCurrencyRequired);
        RiskObject.RegisterRiskObject(Guid.CreateVersion7(), schema, values, "{}", null, null, new(null, null, null), Today, Now, "ZAR").Error!.Code
            .ShouldBe(GeneralErrorCodes.ValuationCurrencyRequired);

        var risk = RiskObject.RegisterRiskObject(Guid.CreateVersion7(), schema, values, "{}", null, null, new(null, null, null), Today, Now).Value;
        risk.RevalueRiskObject(10m, "MARKET", "RAND").Error!.Code.ShouldBe(GeneralErrorCodes.ValuationCurrencyRequired);
        risk.RevalueRiskObject(10m, "MARKET", null).Error!.Code.ShouldBe(GeneralErrorCodes.ValuationCurrencyRequired);
        risk.RevalueRiskObject(10m, "MARKET", "USD").IsSuccess.ShouldBeTrue();
        risk.ValuationCurrency.ShouldBe("USD");
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
        quote.ReferQuote("HIGH_VALUE", Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        quote.IssueQuote(Today.AddDays(30), Now).IsFailure.ShouldBeTrue();

        quote.DecideReferral(false, "DECLINE", [], " ", Guid.CreateVersion7(), Now).Error!.Code.ShouldBe(GeneralErrorCodes.RationaleRequired);
        quote.DecideReferral(false, "DECLINE", [], "Vehicle modified beyond appetite", Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();

        quote.Status.ShouldBe(QuoteStatus.Declined);
        quote.DomainEvents.OfType<GeneralQuoteDeclined>().Single().Reason.ShouldBe("Vehicle modified beyond appetite");
        quote.Referrals.Single().DeclineReason.ShouldBe("Vehicle modified beyond appetite");
        quote.IssueQuote(Today.AddDays(30), Now).IsFailure.ShouldBeTrue();
        quote.DecideReferral(true, "ACCEPT", [], null, Guid.CreateVersion7(), Now).IsFailure.ShouldBeTrue();
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
        var unexpired = 36_500m * (End.DayNumber - cancelled.DayNumber + 1) / (End.DayNumber - Start.DayNumber + 1);

        policy.CancelPolicy(new CancellationRule("INS_REQUEST", "INSURED", "PRO_RATA", 100m), cancelled, Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        policy.DomainEvents.OfType<GeneralPolicyCancelled>().Single().RefundAmount.ShouldBe(Math.Round(unexpired, 2, MidpointRounding.AwayFromZero) - 100m);

        policy.ReinstatePolicy(Guid.CreateVersion7(), Today, Now).IsSuccess.ShouldBeTrue();
        policy.Status.ShouldBe(PolicyStatus.InForce);
        policy.Cancellations.Single().ReinstatedAt.ShouldNotBeNull();
        policy.CancellationInEffect.ShouldBeNull();
    }

    /// <summary>
    /// BR-037 (decided by Ade, 28 October 2026): a policy issued but not yet in force is cancelled only from inception (a
    /// full pro rata refund, as it gave no cover), is never renewed, and reinstated before its period starts is Issued again.
    /// </summary>
    [Fact]
    public void AnIssuedPolicyNotYetInForce_IsCancelledOnlyFromInception_NeverRenewed_BR037()
    {
        GeneralPolicy Issued() => GeneralPolicy.IssuePolicy(Guid.CreateVersion7(), "GP3", Accepted(36_500m), Billing, null, Start.AddDays(-1), Now).Value;
        var rule = new CancellationRule("INS_REQUEST", "INSURED", "PRO_RATA", 0m);
        var policy = Issued();
        policy.Status.ShouldBe(PolicyStatus.Issued);

        policy.CancelPolicy(rule, Start.AddDays(10), Guid.CreateVersion7(), Now).Error!.Code.ShouldBe(GeneralErrorCodes.OutsidePolicyPeriod);
        policy.CancelPolicy(rule, Start, Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        policy.Status.ShouldBe(PolicyStatus.Cancelled);
        policy.DomainEvents.OfType<GeneralPolicyCancelled>().Single().RefundAmount.ShouldBe(36_500m, "no cover was given");

        policy.ReinstatePolicy(Guid.CreateVersion7(), Start.AddDays(-1), Now).IsSuccess.ShouldBeTrue();
        policy.Status.ShouldBe(PolicyStatus.Issued, "its period has not started");
        policy.StartPeriod(Start).IsSuccess.ShouldBeTrue();

        GeneralPolicy.RenewPolicy(Guid.CreateVersion7(), "GP4", Issued(), Start.AddDays(-1), Now).Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
    }

    /// <summary>BR-037 (decided): whoever referred a quote does not decide its referral; another underwriter does.</summary>
    [Fact]
    public void TheReferrer_DoesNotDecideTheReferral_BR037()
    {
        var quote = Quote();
        quote.RateQuote(Guid.CreateVersion7(), "MOTOR", 1, 3, 12_000m, "ZAR").IsSuccess.ShouldBeTrue();
        var referrer = Guid.CreateVersion7();
        quote.ReferQuote("HIGH_VALUE", referrer, Now).IsSuccess.ShouldBeTrue();

        quote.DecideReferral(true, "ACCEPT", [], null, referrer, Now).Error!.Code.ShouldBe(ErrorCodes.MakerCheckerSamePerson);
        quote.Status.ShouldBe(QuoteStatus.Referred);
        quote.DecideReferral(true, "ACCEPT", [], null, Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Cancellation_WithAnUnspecifiedScale_IsRefused() =>
        Policy().CancelPolicy(new CancellationRule("X", "INSURER", "SHORT_PERIOD", 0), Today, Guid.CreateVersion7(), Now).Error!.Code.ShouldBe(GeneralErrorCodes.RuleNotSpecified);

    /// <summary>OQ-132 (b): cover extended for a late non-renewal's notice runs to the new end at the annual premium pro rata; it expires then, not renewed by the insurer.</summary>
    [Fact]
    public void CoverExtendedForANotice_IsBilledProRata_AndThePolicyExpiresNotRenewedByTheInsurer_OQ132()
    {
        var policy = Policy(premium: 3_650m);
        policy.ExtendForNotice(End, Now).IsFailure.ShouldBeTrue("not past the period end");
        policy.ExtendForNotice(End.AddDays(21), Now).IsSuccess.ShouldBeTrue();
        policy.PeriodEnd.ShouldBe(End.AddDays(21));
        policy.DomainEvents.OfType<GeneralCoverExtended>().Single().ShouldSatisfyAllConditions(e => e.PreviousPeriodEnd.ShouldBe(End),
            e => e.ExtraPremium.ShouldBe(Math.Round(policy.AnnualPremium * 21 / 365m, 2, MidpointRounding.AwayFromZero)));
        policy.ExpirePolicy(End.AddDays(22), PolicyExpiryReasons.NotRenewedByInsurer).IsSuccess.ShouldBeTrue();
        (policy.Status, policy.ExpiryReason).ShouldBe((PolicyStatus.Expired, PolicyExpiryReasons.NotRenewedByInsurer));
    }

    /// <summary>B-001: a broker of record change is rated at once with no premium change, needs the appointment letter before submission, and moves the policy at its date.</summary>
    [Fact]
    public void ABrokerOfRecordChange_NeedsItsLetter_AndMovesThePolicyAtItsDate_B001()
    {
        var policy = Policy();
        var type = new EndorsementType(GeneralEndorsement.IntermediaryType, "NONE", [], false);
        GeneralEndorsement.DraftIntermediaryChange(Guid.CreateVersion7(), "GP1-E001", policy, new EndorsementType("CHANGE_SI", "PRO_RATA", [], false), Today, "BRK-9",
            Guid.CreateVersion7()).IsFailure.ShouldBeTrue("only an INTERMEDIARY endorsement");
        GeneralEndorsement.DraftIntermediaryChange(Guid.CreateVersion7(), "GP1-E001", policy, type, End.AddDays(1), "BRK-9", Guid.CreateVersion7()).Error!.Code
            .ShouldBe(GeneralErrorCodes.OutsidePolicyPeriod);
        var endorsement = GeneralEndorsement.DraftIntermediaryChange(Guid.CreateVersion7(), "GP1-E001", policy, type, Today.AddDays(10), "BRK-9", Guid.CreateVersion7()).Value;
        (endorsement.Status, endorsement.PremiumDelta, endorsement.NewAnnualPremium).ShouldBe((EndorsementStatus.Rated, 0m, (decimal?)policy.AnnualPremium));
        endorsement.SubmitEndorsement(Guid.CreateVersion7(), "ZAR", Now).Error!.Code.ShouldBe(GeneralErrorCodes.AppointmentLetterRequired);
        endorsement.RecordAppointmentLetter(Guid.CreateVersion7()).IsSuccess.ShouldBeTrue();
        endorsement.SubmitEndorsement(Guid.CreateVersion7(), "ZAR", Now).IsSuccess.ShouldBeTrue();
        endorsement.RecordAppointmentLetter(Guid.CreateVersion7()).IsFailure.ShouldBeTrue("not once submitted");

        // OQ-136: future-dated, the policy keeps its broker until the date; then it moves, announced once.
        var previous = policy.IntermediaryCode;
        policy.ChangeIntermediary("BRK-9", Today.AddDays(10), Today, Now).IsSuccess.ShouldBeTrue();
        (policy.IntermediaryCode, policy.PendingIntermediaryCode).ShouldBe((previous, "BRK-9"));
        policy.ApplyPendingIntermediary(Today, Now).IsFailure.ShouldBeTrue("not yet");
        policy.ApplyPendingIntermediary(Today.AddDays(10), Now).IsSuccess.ShouldBeTrue();
        (policy.IntermediaryCode, policy.PendingIntermediaryCode).ShouldBe(("BRK-9", (string?)null));
        policy.DomainEvents.OfType<GeneralPolicyIntermediaryChanged>().Single().ShouldSatisfyAllConditions(e => e.PreviousIntermediaryCode.ShouldBe(previous),
            e => e.EffectiveDate.ShouldBe(Today.AddDays(10)));
    }

    [Fact]
    public void Renewal_IsTheNextPeriod_AndThePolicyBecomesRenewed()
    {
        var policy = Policy();
        var renewal = GeneralPolicy.RenewPolicy(Guid.CreateVersion7(), "GP9", policy, Today, Now).Value;

        renewal.PeriodStart.ShouldBe(End.AddDays(1));
        renewal.PeriodEnd.ShouldBe(new DateOnly(2028, 9, 30));
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

        GeneralEndorsement.DraftEndorsement(Guid.CreateVersion7(), "E1", policy, type, End.AddDays(1), changes, Guid.CreateVersion7()).Error!.Code
            .ShouldBe(GeneralErrorCodes.OutsidePolicyPeriod);
        GeneralEndorsement.DraftEndorsement(Guid.CreateVersion7(), "E1", policy, type, End, changes, Guid.CreateVersion7()).IsSuccess.ShouldBeTrue();
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
        endorsement.PremiumDelta.ShouldBe(Math.Round(3_000m * (End.DayNumber - effective.DayNumber + 1) / (End.DayNumber - Start.DayNumber + 1), 2, MidpointRounding.AwayFromZero));
        policy.ApplyEndorsement(endorsement, Motor, [], Now, Today).IsFailure.ShouldBeTrue();

        endorsement.SubmitEndorsement(maker, "ZAR", Now).IsSuccess.ShouldBeTrue();
        endorsement.Decide(true, Guid.CreateVersion7(), maker, Now).Error!.Code.ShouldBe(ErrorCodes.MakerCheckerSamePerson);
        endorsement.Decide(true, Guid.CreateVersion7(), Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();

        policy.ApplyEndorsement(endorsement, Motor, ["ENDORSEMENT"], Now, Today).IsSuccess.ShouldBeTrue();
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

        policy.ApplyEndorsement(endorsement, Motor, [], Now, Today).Error!.Code.ShouldBe(GeneralErrorCodes.OutsideProductLimits);
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

    /// <summary>
    /// OQ-102 (F010): a settlement below the claimed amount (the assessment less the excess) is a partial approval: it gives
    /// a PARTIAL decision reason, is recorded as PARTLY_APPROVED, and asks for the claimant's rights notice (CLAIM_DECISION_RIGHTS).
    /// </summary>
    [Fact]
    public void SettlementBelowTheClaimedAmount_IsAPartialApproval_WithAPartialReason_AndARightsNotice_OQ102()
    {
        var policy = Policy();
        var claim = GeneralClaim.NotifyLoss(Guid.CreateVersion7(), policy.Id, Vehicle, "OWN_DAMAGE", Today, "COLLISION", "ACC", null, null, Today, Now).Value;
        claim.RegisterClaim(policy, "GEN-P1", Now);
        claim.AppointAssessor(Guid.CreateVersion7(), Now);
        var coverage = policy.Coverage(Vehicle, "OWN_DAMAGE")!;
        CoverageLimit[] sublimit = [new("L", "OWN_DAMAGE", "SUBLIMIT", 20_000m, null, "FIXED", "ZAR")];
        var request = new SettlementRequest(40_000m, 5_000m, Guid.CreateVersion7(), Guid.CreateVersion7(), "bank-hash", "INDEMNITY", "SUBLIMIT", "Sublimit applies");

        claim.RequestSettlement(request, coverage, sublimit, "ZAR", Guid.CreateVersion7(), Now).Error!.Code.ShouldBe(GeneralErrorCodes.RationaleRequired);
        claim.RequestSettlement(request with { DecisionType = "APPROVE" }, coverage, sublimit, "ZAR", Guid.CreateVersion7(), Now).IsFailure.ShouldBeTrue();
        claim.RequestSettlement(request with { DecisionType = ClaimDecisionOutcomes.PartialReasonType }, coverage, sublimit, "ZAR", Guid.CreateVersion7(), Now)
            .IsSuccess.ShouldBeTrue();
        (claim.SettlementAmount, claim.ClaimedAmount).ShouldBe((20_000m, 35_000m));
        claim.NoticeDecisionRights(Guid.CreateVersion7(), "Sublimit", Today, Now).IsFailure.ShouldBeTrue("not decided yet");

        claim.DecideSettlement(true, Guid.CreateVersion7(), Guid.CreateVersion7(), claim.SettlementSubjectHash, Now).IsSuccess.ShouldBeTrue();
        claim.DecisionOutcome.ShouldBe(ClaimDecisionOutcomes.PartlyApproved);
        var holder = Guid.CreateVersion7();
        claim.NoticeDecisionRights(holder, "The vehicle sublimit applies", Today, Now).IsSuccess.ShouldBeTrue();
        claim.DomainEvents.OfType<GeneralClaimDecisionRightsDue>().Single().ShouldSatisfyAllConditions(
            e => e.RecipientPartyId.ShouldBe(holder), e => e.Amount.ShouldBe(20_000m), e => e.ClaimedAmount.ShouldBe(35_000m),
            e => e.DecisionReasons.ShouldBe("The vehicle sublimit applies"));
    }

    /// <summary>A settlement of the full claimed amount is APPROVED and sends no rights notice; a decline is DECLINED (OQ-102).</summary>
    [Fact]
    public void FullSettlement_IsApproved_AndADeclineIsDeclined_OQ102()
    {
        var policy = Policy();
        var claim = GeneralClaim.NotifyLoss(Guid.CreateVersion7(), policy.Id, Vehicle, "OWN_DAMAGE", Today, "COLLISION", "ACC", null, null, Today, Now).Value;
        claim.RegisterClaim(policy, "GEN-P2", Now);
        claim.AppointAssessor(Guid.CreateVersion7(), Now);
        claim.RequestSettlement(new SettlementRequest(40_000m, 5_000m, Guid.CreateVersion7(), Guid.CreateVersion7(), "bank-hash", "INDEMNITY", "SETTLE", "Repair quote accepted"),
            policy.Coverage(Vehicle, "OWN_DAMAGE")!, [], "ZAR", Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        claim.DecideSettlement(true, Guid.CreateVersion7(), Guid.CreateVersion7(), claim.SettlementSubjectHash, Now).IsSuccess.ShouldBeTrue();
        claim.DecisionOutcome.ShouldBe(ClaimDecisionOutcomes.Approved);
        claim.NoticeDecisionRights(Guid.CreateVersion7(), null, Today, Now).IsFailure.ShouldBeTrue();

        var declined = GeneralClaim.NotifyLoss(Guid.CreateVersion7(), policy.Id, Vehicle, "OWN_DAMAGE", Today, "COLLISION", "ACC", null, null, Today, Now).Value;
        declined.RegisterClaim(policy, "GEN-P3", Now);
        declined.AppointAssessor(Guid.CreateVersion7(), Now);
        declined.NoticeDeclineRights(new ClaimRightsNotice(Guid.CreateVersion7(), "Excluded", "ZAR", Today), Now).IsFailure.ShouldBeTrue("not declined yet");
        declined.DeclineClaim("EXCLUDED", "Excluded peril", Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        declined.DecisionOutcome.ShouldBe(ClaimDecisionOutcomes.Declined);

        // OQ-102 (Ade, 29 October 2026): any decision that declines all or part of a claim sends the rights, with the reason's wording.
        var holder = Guid.CreateVersion7();
        declined.NoticeDeclineRights(new ClaimRightsNotice(holder, "Your policy does not cover this peril.", "ZAR", Today), Now).IsSuccess.ShouldBeTrue();
        declined.DomainEvents.OfType<GeneralClaimDecisionRightsDue>().Single().ShouldSatisfyAllConditions(e => e.RecipientPartyId.ShouldBe(holder),
            e => e.DecisionReasons.ShouldBe("Your policy does not cover this peril."), e => e.Amount.ShouldBe(0m));
    }

    /// <summary>
    /// F011, F013, OQ-103: an upheld dispute reopens a declined claim (without closing it first) or a closed one; a claim
    /// already open is left as it is, so a redelivery changes nothing.
    /// </summary>
    [Fact]
    public void UpheldDispute_ReopensADeclinedOrClosedClaim_AndLeavesAnOpenOneAlone_OQ103()
    {
        var policy = Policy();
        var claim = GeneralClaim.NotifyLoss(Guid.CreateVersion7(), policy.Id, Vehicle, "OWN_DAMAGE", Today, "COLLISION", "ACC", null, null, Today, Now).Value;
        claim.RegisterClaim(policy, "GEN-R1", Now);
        claim.AppointAssessor(Guid.CreateVersion7(), Now);
        claim.ReopenForCase("CASE-1").ShouldBeFalse("a claim under assessment is open");
        claim.DeclineClaim("EXCLUDED", "Excluded peril", Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();

        claim.ReopenForCase("").ShouldBeFalse("the case is the reason");
        claim.ReopenForCase("CASE-1").ShouldBeTrue();
        claim.Status.ShouldBe(ClaimStatus.Reopened);
        claim.ReopenForCase("CASE-1").ShouldBeFalse("a redelivery changes nothing");
        claim.AppointAssessor(Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        claim.Status.ShouldBe(ClaimStatus.UnderAssessment);

        claim.DeclineClaim("EXCLUDED", "Excluded peril", Guid.CreateVersion7(), Now);
        claim.CloseClaim(Now).IsSuccess.ShouldBeTrue();
        claim.ReopenForCase("CASE-2").ShouldBeTrue();
        claim.Status.ShouldBe(ClaimStatus.Reopened);
    }

    /// <summary>F062 §2 (GEN-008): a settlement over what remains of the coverage's aggregate claims limit is refused.</summary>
    [Fact]
    public void Settlement_IsRefusedOverTheRemainingAggregate_F062()
    {
        var policy = Policy();
        var claim = GeneralClaim.NotifyLoss(Guid.CreateVersion7(), policy.Id, Vehicle, "OWN_DAMAGE", Today, "COLLISION", "ACC", null, null, Today, Now).Value;
        claim.RegisterClaim(policy, "GEN-2", Now);
        claim.AppointAssessor(Guid.CreateVersion7(), Now);
        var coverage = policy.Coverage(Vehicle, "OWN_DAMAGE")!;
        var request = new SettlementRequest(40_000m, 5_000m, Guid.CreateVersion7(), Guid.CreateVersion7(), "bank-hash", "INDEMNITY", "SETTLE", "Repair quote accepted");

        var refused = claim.RequestSettlement(request, coverage, [], "ZAR", Guid.CreateVersion7(), Now, aggregateRemaining: 30_000m);
        refused.Error!.Code.ShouldBe(GeneralErrorCodes.AggregateLimitExceeded);
        refused.Error.Message.ShouldContain("30000.00 ZAR remaining");
        claim.SettlementAmount.ShouldBeNull();
        claim.RequestSettlement(request, coverage, [], "ZAR", Guid.CreateVersion7(), Now, aggregateRemaining: 35_000m).IsSuccess.ShouldBeTrue();
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

        claim.RequestSettlement(new SettlementRequest(40_000m, 5_000m, Guid.CreateVersion7(), Guid.CreateVersion7(), "bank-hash", "INDEMNITY", "SETTLE", " "), coverage, [], "ZAR", maker, Now).Error!.Code
            .ShouldBe(GeneralErrorCodes.RationaleRequired);
        claim.RequestSettlement(new SettlementRequest(4_000m, 5_000m, Guid.CreateVersion7(), Guid.CreateVersion7(), "bank-hash", "INDEMNITY", "SETTLE", "Excess exceeds loss"), coverage, [], "ZAR", maker, Now)
            .Error!.Code.ShouldBe(GeneralErrorCodes.NothingToSettle);
        claim.RequestSettlement(new SettlementRequest(40_000m, 5_000m, Guid.CreateVersion7(), Guid.CreateVersion7(), "bank-hash", "INDEMNITY", "SETTLE", "Repair quote accepted"), coverage, [], "ZAR", maker, Now)
            .IsSuccess.ShouldBeTrue();
        claim.SettlementAmount.ShouldBe(35_000m);
        claim.Status.ShouldBe(ClaimStatus.UnderAssessment);

        claim.DecideSettlement(true, Guid.CreateVersion7(), maker, claim.SettlementSubjectHash, Now).Error!.Code.ShouldBe(ErrorCodes.MakerCheckerSamePerson);
        claim.DecideSettlement(true, Guid.CreateVersion7(), Guid.CreateVersion7(), "another subject", Now).Error!.Code.ShouldBe(GeneralErrorCodes.ApprovalSubjectChanged);
        claim.DecideSettlement(false, Guid.CreateVersion7(), Guid.CreateVersion7(), claim.SettlementSubjectHash, Now).IsSuccess.ShouldBeTrue();
        claim.Status.ShouldBe(ClaimStatus.UnderAssessment);
        claim.SettlementAmount.ShouldBeNull();

        claim.RequestSettlement(new SettlementRequest(40_000m, 5_000m, Guid.CreateVersion7(), Guid.CreateVersion7(), "bank-hash", "INDEMNITY", "SETTLE", "Repair quote accepted"), coverage, [], "ZAR", maker, Now);
        claim.DecideSettlement(true, Guid.CreateVersion7(), Guid.CreateVersion7(), claim.SettlementSubjectHash, Now).IsSuccess.ShouldBeTrue();
        claim.Status.ShouldBe(ClaimStatus.Approved);
        claim.DomainEvents.OfType<GeneralClaimApproved>().Single().ShouldSatisfyAllConditions(e => e.Amount.ShouldBe(35_000m), e => e.PayeeBankHash.ShouldBe("bank-hash"));

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
        LossAssessment.AssignAssessor(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), onApprovedPanel: false, "ZAR").Error!.Code
            .ShouldBe(GeneralErrorCodes.AssessorNotOnPanel);

        var assessment = LossAssessment.AssignAssessor(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), onApprovedPanel: true, "ZAR").Value;
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
        var recovery = RecoveryCase.OpenRecovery(Guid.CreateVersion7(), Guid.CreateVersion7(), "SUBROGATION", Guid.CreateVersion7(), 20_000m, "ZAR").Value;
        recovery.RecordRecovery(15_000m, "GEN-1", "ZAR", "INDEMNITY", Now).IsFailure.ShouldBeTrue();
        recovery.SendDemand().IsSuccess.ShouldBeTrue();
        recovery.AddSalvageItem("Vehicle wreck").IsSuccess.ShouldBeTrue();
        recovery.DisposeSalvage(recovery.Salvage.Single().Id, "AUCTION", 3_000m).IsSuccess.ShouldBeTrue();

        recovery.RecordRecovery(15_000m, "GEN-1", "ZAR", "INDEMNITY", Now).IsSuccess.ShouldBeTrue();
        recovery.DomainEvents.OfType<RecoveryRecorded>().Single().ShouldSatisfyAllConditions(e => e.Amount.ShouldBe(15_000m), e => e.ClaimNumber.ShouldBe("GEN-1"));
        recovery.CloseRecovery().IsSuccess.ShouldBeTrue();
        recovery.AddSalvageItem("late").IsFailure.ShouldBeTrue();
    }
    [Fact]
    public void PeriodEnd_IsTheLastDayOfCover_FirstAndLastDaysCovered_OQ059()
    {
        var policy = Policy();
        GeneralClaim Notify(DateOnly loss) =>
            GeneralClaim.NotifyLoss(Guid.CreateVersion7(), policy.Id, Vehicle, "OWN_DAMAGE", loss, "COLLISION", "ACC", null, null, End.AddDays(5), Now).Value;

        Notify(Start).RegisterClaim(policy, "GEN-1", Now).IsSuccess.ShouldBeTrue();
        Notify(End).RegisterClaim(policy, "GEN-2", Now).IsSuccess.ShouldBeTrue();
        Notify(End.AddDays(1)).RegisterClaim(policy, "GEN-3", Now).Error!.Code.ShouldBe(GeneralErrorCodes.NotCovered);
        Notify(Start.AddDays(-1)).RegisterClaim(policy, "GEN-4", Now).Error!.Code.ShouldBe(GeneralErrorCodes.NotCovered);

        policy.PeriodDays.ShouldBe(365);
        policy.ExpirePolicy(End).IsFailure.ShouldBeTrue();
        policy.ExpirePolicy(End.AddDays(1)).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData("2026-10-01", 36_500)]
    [InlineData("2027-09-30", 100)]
    [InlineData("2027-04-01", 18_300)]
    public void ProRataRefund_CountsTheEffectiveDayAndTheLastDay(string effective, double expected)
    {
        var policy = Policy(36_500m);

        policy.CancelPolicy(new CancellationRule("INS_REQUEST", "INSURED", "PRO_RATA", 0), DateOnly.Parse(effective), Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();

        policy.DomainEvents.OfType<GeneralPolicyCancelled>().Single().RefundAmount.ShouldBe((decimal)expected);
    }

    [Fact]
    public void LeapYear_HasThreeHundredAndSixtySixDays_AndRenewsToTheSameDayNextYear()
    {
        var leap = GeneralQuote.CreateQuote(Guid.CreateVersion7(), "GQ-L", null, Motor, Guid.CreateVersion7(), null, "ZAR", new DateOnly(2027, 3, 1),
            new DateOnly(2028, 2, 29), [Risk()], Today).Value;
        leap.RateQuote(Guid.CreateVersion7(), "MOTOR", 1, 3, 36_600m, "ZAR");
        leap.IssueQuote(Today.AddDays(30), Now);
        leap.AcceptQuote(Today, Now);
        var policy = GeneralPolicy.IssuePolicy(Guid.CreateVersion7(), "GPL", leap, Billing, null, new DateOnly(2027, 3, 1), Now).Value;

        policy.PeriodDays.ShouldBe(366);
        policy.CancelPolicy(new CancellationRule("X", "INSURED", "PRO_RATA", 0), new DateOnly(2028, 2, 29), Guid.CreateVersion7(), Now);
        policy.DomainEvents.OfType<GeneralPolicyCancelled>().Single().RefundAmount.ShouldBe(100m);

        GeneralPolicy.NextPeriodEnd(new DateOnly(2027, 3, 1), new DateOnly(2028, 2, 29), new DateOnly(2028, 3, 1)).ShouldBe(new DateOnly(2029, 2, 28));
        GeneralPolicy.NextPeriodEnd(new DateOnly(2027, 10, 1), new DateOnly(2027, 10, 14), new DateOnly(2027, 10, 15)).ShouldBe(new DateOnly(2027, 10, 28));
        GeneralPolicy.NextPeriodEnd(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 2)).ShouldBe(new DateOnly(2026, 10, 2));
    }

    [Fact]
    public void CoverStartingOnFirstPayment_WaitsForIt()
    {
        var policy = GeneralPolicy.IssuePolicy(Guid.CreateVersion7(), "GPF", Accepted(), Billing, null, Today, Now, "FIRST_PAYMENT").Value;

        policy.Status.ShouldBe(PolicyStatus.Issued);
        policy.StartPeriod(Today).IsFailure.ShouldBeTrue();
        policy.CoversLoss(Vehicle, "OWN_DAMAGE", Today).Error!.Code.ShouldBe(GeneralErrorCodes.NotCovered);

        var paidOn = Today.AddDays(3);
        policy.RecordFirstPayment(paidOn, paidOn).IsSuccess.ShouldBeTrue();
        policy.Status.ShouldBe(PolicyStatus.InForce);
        policy.CoversLoss(Vehicle, "OWN_DAMAGE", paidOn.AddDays(-1)).Error!.Code.ShouldBe(GeneralErrorCodes.NotCovered);
        policy.CoversLoss(Vehicle, "OWN_DAMAGE", paidOn).IsSuccess.ShouldBeTrue();
        policy.RecordFirstPayment(paidOn.AddDays(30), paidOn.AddDays(30)).IsSuccess.ShouldBeTrue();
        policy.FirstPaidOn.ShouldBe(paidOn);
    }

    /// <summary>Wave 2 rule: every ending of an endorsement's approval (rejected, expired, withdrawn) rejects it, once (D-5).</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Endorsement_EveryEndingOtherThanApproval_RejectsIt_AndARedeliveryIsAlreadyApplied(bool withChecker)
    {
        var policy = Policy();
        var maker = Guid.CreateVersion7();
        var type = new EndorsementType("CHANGE_SI", "FLAT", [], false);
        var endorsement = GeneralEndorsement.DraftEndorsement(Guid.CreateVersion7(), "E1", policy, type, Today,
            [new(EndorsementAction.ChangeRisk, Vehicle, null, [new CoverageRequest("OWN_DAMAGE", 300_000m, null)])], maker).Value;
        endorsement.RateEndorsement(Guid.CreateVersion7(), 15_000m, policy, type).IsSuccess.ShouldBeTrue();
        endorsement.SubmitEndorsement(maker, "ZAR", Now).IsSuccess.ShouldBeTrue();
        var approval = Guid.CreateVersion7();
        var checker = withChecker ? Guid.CreateVersion7() : Guid.Empty;

        endorsement.Decide(true, approval, Guid.Empty, Now).IsFailure.ShouldBeTrue("an approval has a checker");
        endorsement.EndingApplied(approval).ShouldBeFalse();
        endorsement.Decide(false, approval, checker, Now).IsSuccess.ShouldBeTrue();

        endorsement.Status.ShouldBe(EndorsementStatus.Rejected);
        endorsement.DecidedBy.ShouldBe(withChecker ? checker : null);
        endorsement.EndingApplied(approval).ShouldBeTrue();
        endorsement.EndingApplied(Guid.CreateVersion7()).ShouldBeFalse("another approval's ending is not applied");
        endorsement.Decide(false, approval, checker, Now).IsFailure.ShouldBeTrue("the handler parks this, unless already applied");
    }

    /// <summary>Wave 2 rule: every ending of a settlement's approval leaves the claim under assessment with the settlement cleared, once (D-5).</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Settlement_EveryEndingOtherThanApproval_KeepsTheClaimUnderAssessment_AndARedeliveryIsAlreadyApplied(bool withChecker)
    {
        var policy = Policy();
        var claim = GeneralClaim.NotifyLoss(Guid.CreateVersion7(), policy.Id, Vehicle, "OWN_DAMAGE", Today, "COLLISION", "ACC", null, null, Today, Now).Value;
        claim.RegisterClaim(policy, "GEN-3", Now);
        claim.AppointAssessor(Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        var maker = Guid.CreateVersion7();
        claim.RequestSettlement(new SettlementRequest(40_000m, 5_000m, Guid.CreateVersion7(), Guid.CreateVersion7(), "bank-hash", "INDEMNITY", "SETTLE", "Repair quote accepted"),
            policy.Coverage(Vehicle, "OWN_DAMAGE")!, [], "ZAR", maker, Now).IsSuccess.ShouldBeTrue();
        var approval = Guid.CreateVersion7();
        var checker = withChecker ? Guid.CreateVersion7() : Guid.Empty;

        claim.DecideSettlement(true, approval, Guid.Empty, claim.SettlementSubjectHash, Now).IsFailure.ShouldBeTrue("an approval has a checker");
        claim.DecideSettlement(false, approval, checker, "another subject", Now).Error!.Code.ShouldBe(GeneralErrorCodes.ApprovalSubjectChanged);
        claim.SettlementEndingApplied(approval).ShouldBeFalse();
        claim.DecideSettlement(false, approval, checker, claim.SettlementSubjectHash, Now).IsSuccess.ShouldBeTrue();

        (claim.Status, claim.SettlementAmount, claim.SettlementPending).ShouldBe((ClaimStatus.UnderAssessment, (decimal?)null, false));
        claim.SettlementEndingApplied(approval).ShouldBeTrue();
        claim.DecideSettlement(false, approval, checker, null, Now).IsFailure.ShouldBeTrue("the handler parks this, unless already applied");
    }

    /// <summary>
    /// F006: a missed instalment starts grace from the oldest unpaid due date for the product's days, telling the policyholder
    /// when cover ends unless paid; a later instalment missed in the same grace adds no second notice; a payment clears it.
    /// </summary>
    [Fact]
    public void Grace_StartsAtTheOldestUnpaidInstalment_NoticeOnce_AndAPaymentClearsIt_F006()
    {
        var policy = Policy();
        var due = new DateOnly(2026, 11, 1);
        policy.RecordArrears(1, due, new GraceTerms(15, true, "NON_PAYMENT"), Now).IsSuccess.ShouldBeTrue();
        (policy.ArrearsSince, policy.GraceUntil, policy.ArrearsCount).ShouldBe((due, due.AddDays(15), 1));
        var notice = policy.DomainEvents.OfType<GeneralPolicyEnteredGrace>().Single();
        (notice.GraceUntil, notice.CoverEndsOn, notice.CoverDuringGrace).ShouldBe((due.AddDays(15), due.AddDays(15), true));

        policy.RecordArrears(2, due, new GraceTerms(15, true, "NON_PAYMENT"), Now).IsSuccess.ShouldBeTrue();
        policy.ArrearsCount.ShouldBe(2);
        policy.DomainEvents.OfType<GeneralPolicyEnteredGrace>().Count().ShouldBe(1, "the same grace");
        policy.LossAwaitsArrears(due.AddDays(3)).ShouldBeFalse("cover continues in grace");

        policy.RecordArrears(0, null, new GraceTerms(15, true, "NON_PAYMENT"), Now).IsSuccess.ShouldBeTrue();
        (policy.ArrearsSince, policy.GraceUntil, policy.ArrearsCount).ShouldBe(((DateOnly?)null, (DateOnly?)null, 0));
    }

    /// <summary>
    /// F006, addendum §5 (Ade, 29 October 2026): without cover in grace, a loss from the first unpaid due date is accepted;
    /// its payment waits for the arrears. The notice says cover ended the day before.
    /// </summary>
    [Fact]
    public void Grace_WithoutCover_ALossInItIsAccepted_ItsPaymentWaitsForTheArrears_F006()
    {
        var policy = Policy();
        var due = Today.AddDays(-5);
        policy.RecordArrears(1, due, new GraceTerms(15, false, "NON_PAYMENT"), Now).IsSuccess.ShouldBeTrue();
        policy.DomainEvents.OfType<GeneralPolicyEnteredGrace>().Single().CoverEndsOn.ShouldBe(due.AddDays(-1));
        policy.CoversLoss(null, "OWN_DAMAGE", Today).IsSuccess.ShouldBeTrue("notification and registration always succeed");
        policy.LossAwaitsArrears(Today).ShouldBeTrue();
        policy.LossAwaitsArrears(due.AddDays(-1)).ShouldBeFalse("before the unpaid instalment");

        policy.RecordArrears(0, null, new GraceTerms(15, false, "NON_PAYMENT"), Now);
        policy.LossAwaitsArrears(Today).ShouldBeFalse("paid");
        policy.DomainEvents.OfType<GeneralPolicyArrearsCleared>().Count().ShouldBe(1, "held payments are released");
    }

    /// <summary>
    /// F006: the approved settlement of a loss in grace without cover names the hold (PREMIUM_ARREARS), or, by the tenant's
    /// rule, is paid less the arrears, which Billing settles from it.
    /// </summary>
    [Fact]
    public void ASettlementInGrace_IsHeldForTheArrears_OrPaidLessThem_F006()
    {
        GeneralClaim Approved(GeneralPolicy policy, SettlementCondition? condition)
        {
            var claim = GeneralClaim.NotifyLoss(Guid.CreateVersion7(), policy.Id, Vehicle, "OWN_DAMAGE", Today, "COLLISION", "ACC", null, null, Today, Now).Value;
            claim.RegisterClaim(policy, "GEN-G1", Now).IsSuccess.ShouldBeTrue();
            claim.AppointAssessor(Guid.CreateVersion7(), Now);
            claim.RequestSettlement(new SettlementRequest(40_000m, 5_000m, Guid.CreateVersion7(), Guid.CreateVersion7(), "bank-hash", "INDEMNITY", "SETTLE", "Repair quote"),
                policy.Coverage(Vehicle, "OWN_DAMAGE")!, [], "ZAR", Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
            claim.DecideSettlement(true, Guid.CreateVersion7(), Guid.CreateVersion7(), claim.SettlementSubjectHash, Now, condition).IsSuccess.ShouldBeTrue();
            return claim;
        }

        var policy = Policy();
        var held = Approved(policy, new SettlementCondition(policy.Id, true, 0m));
        var approved = held.DomainEvents.OfType<GeneralClaimApproved>().Single();
        (approved.HoldReason, approved.Amount, approved.PremiumOffset, approved.PolicyId).ShouldBe((SettlementCondition.PremiumArrears, held.SettlementAmount!.Value, 0m, (Guid?)policy.Id));
        held.DomainEvents.OfType<GeneralPremiumOffsetFromClaim>().ShouldBeEmpty();

        var netted = Approved(policy, new SettlementCondition(policy.Id, false, 1_250m));
        var paid = netted.DomainEvents.OfType<GeneralClaimApproved>().Single();
        (paid.HoldReason, paid.Amount, paid.PremiumOffset).ShouldBe(((string?)null, netted.SettlementAmount!.Value - 1_250m, 1_250m));
        netted.DomainEvents.OfType<GeneralPremiumOffsetFromClaim>().Single().ShouldSatisfyAllConditions(e => e.Amount.ShouldBe(1_250m), e => e.PolicyId.ShouldBe(policy.Id));
        // OQ-137: the claim keeps the deduction; settled, it reports the gross and the deduction.
        netted.PremiumOffset.ShouldBe(1_250m);
        netted.SettleClaim(Today, Now).IsSuccess.ShouldBeTrue();
        netted.DomainEvents.OfType<GeneralClaimSettled>().Single().ShouldSatisfyAllConditions(e => e.Amount.ShouldBe(netted.SettlementAmount!.Value),
            e => e.PremiumOffset.ShouldBe(1_250m));

        // Grace ended unpaid: declined with the rule's claim decision even once approved; a later approval decision is moot.
        var holder = Guid.CreateVersion7();
        held.DeclineForNonPayment("NON_PAYMENT", Today.AddDays(-5), Now, new ClaimRightsNotice(holder, "The premium was not paid.", "ZAR", Today)).IsSuccess.ShouldBeTrue();
        // OQ-102: a claim declined in full sends the claimant their rights, with the reason's wording.
        held.DomainEvents.OfType<GeneralClaimDecisionRightsDue>().Single().ShouldSatisfyAllConditions(e => e.RecipientPartyId.ShouldBe(holder),
            e => e.DecisionReasons.ShouldBe("The premium was not paid."), e => e.Amount.ShouldBe(0m));
        (held.Status, held.DecisionCode, held.DeclinedForNonPayment).ShouldBe((ClaimStatus.Declined, "NON_PAYMENT", true));
        held.DomainEvents.OfType<GeneralClaimDeclined>().Single().DecisionCode.ShouldBe("NON_PAYMENT");
        held.DecideSettlement(true, Guid.CreateVersion7(), Guid.CreateVersion7(), null, Now).IsSuccess.ShouldBeTrue("moot");
        netted.DeclineForNonPayment("NON_PAYMENT", Today.AddDays(1), Now).IsFailure.ShouldBeTrue("the loss is before the cancellation date");
    }

    /// <summary>
    /// F006: grace ending unpaid cancels the policy with the grace rule's reason and tells the policyholder; not before grace
    /// ends. Without cover in grace, from the first unpaid due date; with it, from the day after grace, as the notice said.
    /// Claims for losses from then are declined.
    /// </summary>
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 16)]
    public void CancelForNonPayment_OnlyOnceGraceEnds_FromTheDateTheNoticeGave_F006(bool coverDuringGrace, int fromDaysAfterDue)
    {
        var policy = Policy();
        var due = new DateOnly(2026, 11, 1);
        var rule = new CancellationRule("NON_PAYMENT", "INSURER", "NONE", 0m);
        var grace = new GraceTerms(15, coverDuringGrace, "NON_PAYMENT", "UNPAID");
        policy.RecordArrears(1, due, grace, Now);

        policy.CancelForNonPayment(rule, grace, due.AddDays(15), Now).Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition, "the last day of grace");
        policy.CancelForNonPayment(rule, grace, due.AddDays(16), Now).IsSuccess.ShouldBeTrue();
        policy.Status.ShouldBe(PolicyStatus.Cancelled);
        var from = due.AddDays(fromDaysAfterDue);
        (policy.CancellationInEffect!.EffectiveDate, policy.CancellationInEffect.ReasonCode, policy.CancellationInEffect.CancelledBy).ShouldBe((from, "NON_PAYMENT", (Guid?)null));
        var notice = policy.DomainEvents.OfType<GeneralPolicyCancelledForNonPayment>().Single();
        (notice.EffectiveDate, notice.RefundAmount).ShouldBe((from, 0m));
        var lapsed = policy.DomainEvents.OfType<GeneralPolicyLapsedUnpaid>().Single();
        (lapsed.CancelledFrom, lapsed.ClaimDeclineReason).ShouldBe((from, "UNPAID"));
        policy.ArrearsSince.ShouldBeNull();
    }

    /// <summary>
    /// F007, OQ-133: a short-period refund keeps the scale's per cent of the annual premium for the days in force (the first
    /// band covering them, both edges), less the fee and never below zero; past the last band nothing is refunded; a reason
    /// with no scale is still refused.
    /// </summary>
    [Theory]
    [InlineData(10, 25, 27_375 - 100)]
    [InlineData(30, 25, 27_375 - 100)]
    [InlineData(31, 40, 21_900 - 100)]
    [InlineData(180, 60, 14_600 - 100)]
    [InlineData(200, 100, 0)]
    public void ShortPeriodRefund_KeepsTheBandsPerCent_LessTheFee_NeverBelowZero_F007(int daysInForce, int retainedPct, int refund)
    {
        ShortPeriodBand[] scale = [new(30, 25m), new(90, 40m), new(180, 60m)];
        var policy = Policy(36_500m);
        policy.CancelPolicy(new CancellationRule("INS_SHORT", "INSURED", "SHORT_PERIOD", 100m, scale), Start.AddDays(daysInForce), Guid.CreateVersion7(), Now)
            .IsSuccess.ShouldBeTrue();
        policy.DomainEvents.OfType<GeneralPolicyCancelled>().Single().RefundAmount.ShouldBe(refund, $"{retainedPct}% retained");

        Policy(36_500m).CancelPolicy(new CancellationRule("INS_SHORT", "INSURED", "SHORT_PERIOD", 0m), Start.AddDays(10), Guid.CreateVersion7(), Now)
            .Error!.Code.ShouldBe(GeneralErrorCodes.RuleNotSpecified);
    }
}
