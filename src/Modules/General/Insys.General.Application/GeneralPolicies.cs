/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : General
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
using Insys.Billing.Contracts;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.ClaimsFramework.Contracts;
using Insys.Documents.Contracts;
using Insys.General.Contracts;
using AssessmentStatus = Insys.General.Domain.Claims.AssessmentStatus;
using LossAssessment = Insys.General.Domain.Claims.LossAssessment;
using Insys.ProductFactory.Contracts;
using Insys.Rating.Contracts;
using Insys.Workflow.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Insys.General.Application;

/// <summary>
/// General's policies (domain-model.json: general consumes PremiumCalculated, ApprovalGranted, ClaimPaymentCompleted),
/// and the reactions that keep its own aggregates consistent with each other through its events.
/// </summary>
public sealed class GeneralPolicies(IGeneralRepository general, IGeneralCodes codes, ISellableProducts products, IClaimDecisionReasons reasons, IPolicyArrears arrears,
    ITenantContext tenant, IBusinessDate businessDate, IClock clock, ILogger<GeneralPolicies> logger) :
    IIntegrationEventHandler<PremiumCalculated>,
    IIntegrationEventHandler<PremiumNotCalculated>,
    IIntegrationEventHandler<ApprovalGranted>,
    IIntegrationEventHandler<ApprovalRejected>,
    IIntegrationEventHandler<ClaimPaymentCompleted>,
    IIntegrationEventHandler<DocumentGenerated>,
    IIntegrationEventHandler<GeneralEndorsementApproved>,
    IIntegrationEventHandler<GeneralPolicyEndorsed>,
    IIntegrationEventHandler<GeneralPolicyIssued>,
    IIntegrationEventHandler<AssessorAppointed>,
    IIntegrationEventHandler<InvoiceSettled>
{
    /// <summary>InvoiceSettled → confirm cover where payment is a condition: a policy whose cover starts on first payment comes into force.</summary>
    public async Task Handle(InvoiceSettled settled, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (settled.SourceDomain == GeneralRatingSubjects.SourceDomain && await general.Policy(settled.PolicyRef, cancellationToken) is { CoverStartsOn: "FIRST_PAYMENT" } policy)
        {
            policy.RecordFirstPayment(businessDate.Today, businessDate.Today);
        }
    }

    /// <summary>
    /// PremiumCalculated → attach the premium to the quote or endorsement it was calculated for (RateQuote,
    /// RateEndorsement; PRD-008). A calculation that does not match its subject is logged and not attached.
    /// </summary>
    public async Task Handle(PremiumCalculated calculated, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (calculated.SourceDomain != GeneralRatingSubjects.SourceDomain)
        {
            return;
        }

        BuildingBlocks.Domain.Result result;
        if (calculated.SubjectType == GeneralRatingSubjects.Quote && await general.Quote(calculated.SubjectId, cancellationToken) is { } quote)
        {
            result = quote.RateQuote(calculated.CalculationId, calculated.ProductCode, calculated.ProductVersion, calculated.RatingVersion, calculated.Premium, calculated.Currency);

            // Wave 4 §4.2: a renewal quote's rating is its renewal's re-rated premium.
            if (result.IsSuccess && quote.RenewalId is { } renewalId && await general.Renewal(renewalId, cancellationToken) is { Status: Domain.Renewals.RenewalStatus.Reviewing } renewal)
            {
                renewal.RecordRating(quote.Id, calculated.Premium, clock.UtcNow);
            }

            // BR-095: a quote priced as it is made is checked against its zone and peril limits (F062) as soon as it is
            // rated, as its issue would check it, so a broker never sees a price the rules then withdraw.
            if (result.IsSuccess && quote.PricedOnCreation
                && await ZoneExposure.FirstExceeded(general, codes, quote.Risks.Select(r => (r.RiskObjectId, r.SumInsured)).ToList(), quote.Currency, businessDate.Today,
                    cancellationToken) is not null)
            {
                result = quote.ReferQuoteByRules(ZoneExposure.ReferralReason, clock.UtcNow);
            }
        }
        else if (calculated.SubjectType == GeneralRatingSubjects.Endorsement && await general.Endorsement(calculated.SubjectId, cancellationToken) is { } endorsement
                 && await general.Policy(endorsement.PolicyId, cancellationToken) is { } policy
                 && await codes.EndorsementType(endorsement.EndorsementType, businessDate.Today, cancellationToken) is { } type)
        {
            result = calculated.ProductCode == policy.ProductCode && calculated.Currency == policy.Currency
                ? endorsement.RateEndorsement(calculated.CalculationId, calculated.Premium, policy, type)
                : BuildingBlocks.Domain.Error.Validation(Domain.GeneralErrorCodes.RatingMismatch, "The calculation is for another product or currency.");
        }
        else
        {
            return;
        }

        if (result.IsFailure)
        {
            logger.LogWarning("Premium calculation {CalculationId} not attached to {SubjectType} {SubjectId}: {Code}", calculated.CalculationId, calculated.SubjectType,
                calculated.SubjectId, result.Error.Code);
        }
    }

    /// <summary>
    /// PremiumNotCalculated → a quote priced as it is made that Rating could not price stays Draft with the reason (BR-095),
    /// for example a rating input its risk objects do not give.
    /// </summary>
    public async Task Handle(PremiumNotCalculated failed, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (failed.SourceDomain == GeneralRatingSubjects.SourceDomain && failed.SubjectType == GeneralRatingSubjects.Quote
            && await general.Quote(failed.SubjectId, cancellationToken) is { } quote)
        {
            if (quote.RecordRatingIssue(failed.ErrorCode, failed.Reason) is { IsFailure: true } refused)
            {
                logger.LogWarning("Rating issue {Code} not recorded on quote {QuoteId}: {Refusal}", failed.ErrorCode, failed.SubjectId, refused.Error.Code);
            }

            // Wave 4 §4.2: a renewal quote Rating could not price refers its renewal to an underwriter.
            if (quote.RenewalId is { } renewalId && await general.Renewal(renewalId, cancellationToken) is { Status: Domain.Renewals.RenewalStatus.Reviewing } renewal)
            {
                renewal.RecordRatingIssue(quote.Id, $"Rating could not price the renewal: {failed.Reason}", clock.UtcNow);
            }
        }
    }

    /// <summary>
    /// ApprovalGranted → ApproveEndorsement or ApproveSettlement. A redelivery of a grant already applied changes nothing; a
    /// record that will not take it (it changed after it was submitted: the settlement is no longer the one approved, or the
    /// endorsement's policy was cancelled, expired or renewed) parks the delivery (APPROVAL_NOT_APPLIED; D-5, ADR-022), and
    /// Workflow marks the approval not applied and tells the requester.
    /// </summary>
    public Task Handle(ApprovalGranted granted, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Decide(granted.BusinessRefType, granted.BusinessRefId, approved: true, granted.ApprovalId, granted.CheckerId, granted.SubjectHash, cancellationToken);

    /// <summary>ApprovalRejected → RejectEndorsement, or the claim stays under assessment.</summary>
    public Task Handle(ApprovalRejected rejected, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Decide(rejected.BusinessRefType, rejected.BusinessRefId, approved: false, rejected.ApprovalId, rejected.CheckerId, rejected.SubjectHash, cancellationToken);

    /// <summary>ClaimPaymentCompleted → SettleClaim.</summary>
    public async Task Handle(ClaimPaymentCompleted completed, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (completed.Domain == "GENERAL" && await general.ClaimByNumber(completed.ClaimNumber, cancellationToken) is { } claim)
        {
            claim.SettleClaim(completed.PaidOn, clock.UtcNow);
        }
    }

    /// <summary>The endorsement's document, once generated (GEN-003).</summary>
    public async Task Handle(DocumentGenerated generated, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (generated.BusinessRefType == GeneralDocuments.EndorsementRefType && await general.Endorsement(generated.BusinessRefId, cancellationToken) is { DocumentId: null } endorsement)
        {
            endorsement.AttachDocument(generated.DocumentId);
        }
    }

    /// <summary>An approved endorsement is applied to the policy (ApplyEndorsement).</summary>
    public async Task Handle(GeneralEndorsementApproved approved, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (await general.Endorsement(approved.EndorsementId, cancellationToken) is not { } endorsement || await general.Policy(approved.PolicyId, cancellationToken) is not { } policy)
        {
            return;
        }

        var product = await Products.Issued(products, tenant.RequireTenantId(), policy.ProductCode, policy.ProductVersion, businessDate.Today, cancellationToken);
        var type = await codes.EndorsementType(endorsement.EndorsementType, businessDate.Today, cancellationToken);
        var applied = product.IsFailure ? product : policy.ApplyEndorsement(endorsement, product.Value, type?.Documents ?? [], clock.UtcNow);
        if (applied.IsFailure)
        {
            // Approved but not applicable (a limit breached since, say): retried, then dead-lettered for a person.
            throw new InvalidOperationException($"Endorsement {endorsement.EndorsementNumber} could not be applied: {applied.Error.Message}");
        }
    }

    /// <summary>The policy applied the endorsement: transition "Policy updated".</summary>
    public async Task Handle(GeneralPolicyEndorsed endorsed, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (await general.Endorsement(endorsed.EndorsementId, cancellationToken) is { } endorsement)
        {
            endorsement.MarkApplied();
        }
    }

    /// <summary>A renewal's policy is issued: the policy it renews becomes Renewed; the quote's proposal is converted.</summary>
    public async Task Handle(GeneralPolicyIssued issued, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (issued.RenewalOfId is { } renewed && await general.Policy(renewed, cancellationToken) is { Status: Domain.Policies.PolicyStatus.InForce } expiring)
        {
            expiring.MarkRenewed(issued.PolicyId, clock.UtcNow);
        }

        if (await general.Policy(issued.PolicyId, cancellationToken) is { QuoteId: { } quoteId } && await general.ProposalForQuote(quoteId, cancellationToken) is { } proposal)
        {
            proposal.Convert();
        }
    }

    /// <summary>An assessor was appointed: the loss assessment is assigned (the panel was checked when appointing).</summary>
    public async Task Handle(AssessorAppointed appointed, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var assessments = await general.Assessments(appointed.ClaimId, cancellationToken);
        if (assessments.Any(a => a.AssessorPartyId == appointed.AssessorPartyId && a.Status != AssessmentStatus.Accepted))
        {
            return;
        }

        // The assessment is in the claim's policy's currency (BR-033).
        var policy = await general.Claim(appointed.ClaimId, cancellationToken) is { } claim ? await general.Policy(claim.PolicyId, cancellationToken) : null;
        if (policy is null)
        {
            throw new InvalidOperationException($"The claim {appointed.ClaimId} an assessor was appointed to, or its policy, was not found.");
        }

        var assessment = LossAssessment.AssignAssessor(Guid.CreateVersion7(), appointed.ClaimId, appointed.AssessorPartyId, onApprovedPanel: true, policy.Currency);
        general.Add(assessment.Value);
    }

    private async Task Decide(string businessRefType, Guid id, bool approved, Guid approvalId, Guid checkerId, string? subjectHash, CancellationToken cancellationToken)
    {
        if (businessRefType == GeneralApprovals.EndorsementBusinessRefType && await general.Endorsement(id, cancellationToken) is { } endorsement)
        {
            if (approved ? endorsement.ApprovalApplied(approvalId) : endorsement.EndingApplied(approvalId))
            {
                return;
            }

            // A grant for a policy that no longer takes endorsements would be approved and never applied.
            var decided = approved && await general.Policy(endorsement.PolicyId, cancellationToken) is not { TakesEndorsements: true }
                ? BuildingBlocks.Domain.Error.Conflict(BuildingBlocks.Domain.ErrorCodes.InvalidStateTransition, "The endorsement's policy no longer takes endorsements.")
                : endorsement.Decide(approved, approvalId, checkerId, clock.UtcNow);
            NotApplied(decided, approved, GeneralApprovals.EndorsementApprovalType, approvalId);
        }
        else if (businessRefType == GeneralApprovals.NonRenewalBusinessRefType && await general.Renewal(id, cancellationToken) is { } renewal)
        {
            // Wave 4 §4.3 (F025): the second person's decision on a non-renewal. A redelivery, or a decision after the renewal moved on, changes nothing.
            if (renewal.NonRenewalStatus != Domain.Renewals.NonRenewalStates.Requested || await general.Policy(renewal.PolicyId, cancellationToken) is not { } policy
                || await codes.NonRenewalReason(renewal.NonRenewalReason!, businessDate.Today, cancellationToken) is not { } reason)
            {
                return;
            }

            var decided = NonRenewals.Decide(renewal, policy, approved, checkerId, reason, businessDate.Today, clock.UtcNow);
            NotApplied(decided, approved, GeneralApprovals.NonRenewalApprovalType, approvalId);
        }
        else if (businessRefType == GeneralApprovals.SettlementBusinessRefType && await general.Claim(id, cancellationToken) is { } claim)
        {
            if (approved ? claim.SettlementApprovalApplied(approvalId) : claim.SettlementEndingApplied(approvalId))
            {
                return;
            }

            var decided = claim.DecideSettlement(approved, approvalId, checkerId, subjectHash, clock.UtcNow,
                approved ? await ArrearsCondition(claim, cancellationToken) : null);
            NotApplied(decided, approved, GeneralApprovals.SettlementApprovalType, approvalId);

            // OQ-102: a partial approval sends the policyholder their rights on the declined part, with the reason's wording.
            if (approved && claim.PartlyApproved && await general.Policy(claim.PolicyId, cancellationToken) is { } policy)
            {
                var reason = await reasons.InForce(tenant.RequireTenantId(), claim.DecisionCode!, businessDate.Today, cancellationToken);
                claim.NoticeDecisionRights(policy.PolicyholderPartyId, reason?.CustomerWording, businessDate.Today, clock.UtcNow);
            }
        }
    }

    /// <summary>
    /// F006: a loss in grace without cover, with the arrears unpaid, is paid only once they are: the payment is held for them,
    /// or, where the tenant's grace rule says so and the settlement covers them, paid less them (Billing settles the arrears).
    /// </summary>
    private async Task<Domain.Claims.SettlementCondition?> ArrearsCondition(Domain.Claims.GeneralClaim claim, CancellationToken cancellationToken)
    {
        if (await general.Policy(claim.PolicyId, cancellationToken) is not { } policy || !policy.LossAwaitsArrears(claim.LossDate))
        {
            return null;
        }

        var owed = (await arrears.For(tenant.RequireTenantId(), GeneralRatingSubjects.SourceDomain, policy.Id, businessDate.Today, cancellationToken)).Outstanding;
        var grace = await codes.GraceTerms(policy.ProductCode, policy.BillingFrequency ?? "", businessDate.Today, cancellationToken);
        return grace is { DeductArrearsFromClaims: true } && owed > 0 && owed < claim.SettlementAmount
            ? new Domain.Claims.SettlementCondition(policy.Id, false, owed)
            : new Domain.Claims.SettlementCondition(policy.Id, true, 0m);
    }

    /// <summary>
    /// A decision the record would not take parks the delivery (D-5, ADR-022), so it is retried and escalated rather than
    /// left waiting or only logged: an ending (rejected, expired, withdrawn) as APPROVAL_ENDING_NOT_APPLIED, a grant as
    /// APPROVAL_NOT_APPLIED.
    /// </summary>
    private static void NotApplied(BuildingBlocks.Domain.Result decided, bool approved, string approvalType, Guid approvalId)
    {
        if (decided.IsFailure)
        {
            throw new PrerequisiteMissingException(approved ? PrerequisiteCodes.ApprovalNotApplied : PrerequisiteCodes.ApprovalEndingNotApplied,
                $"{approvalType}:{approvalId}");
        }
    }
}

public sealed record DueWork(IReadOnlyList<Guid> PoliciesToStart, IReadOnlyList<Guid> PoliciesToExpire, IReadOnlyList<Guid> QuotesToExpire,
    IReadOnlyList<Guid> PoliciesToInvite, IReadOnlyList<Guid> PoliciesToRenew, IReadOnlyList<RenewalReminder>? RenewalReminders = null,
    IReadOnlyList<Guid>? PoliciesPastGrace = null, IReadOnlyList<Guid>? PoliciesToReview = null);

/// <summary>A renewal reminder due today: the policy, and how many days before its period ends (OQ-089).</summary>
public sealed record RenewalReminder(Guid PolicyId, int DaysBefore);

/// <summary>Remind a policyholder who has not renewed (communications §6.2): the policy announces it; Communications sends it.</summary>
public sealed record RemindRenewal(Guid PolicyId, int DaysBefore, DateOnly Day) : ICommand<bool>;

internal sealed class RemindRenewalHandler(IGeneralRepository general, IClock clock) : ICommandHandler<RemindRenewal, bool>
{
    public async Task<BuildingBlocks.Domain.Result<bool>> Handle(RemindRenewal command, CancellationToken cancellationToken)
    {
        if (await general.Policy(command.PolicyId, cancellationToken) is not { } policy)
        {
            return false;
        }

        var result = policy.RemindRenewal(command.DaysBefore, command.Day, clock.UtcNow);
        return result.IsFailure ? result.Error : true;
    }
}

/// <summary>
/// The daily jobs' work: policies whose period starts or ends, quotes past validity, renewal invitations due, and policies
/// that renew by themselves (an auto_renew rule that does not re-rate; one that re-rates is renewed through a new quote).
/// </summary>
public sealed record FindDueWork(DateOnly Day, int Limit) : IQuery<DueWork>;

public enum DueAction
{
    StartPeriod,
    ExpirePolicy,
    ExpireQuote,
    InviteRenewal,

    /// <summary>Grace ended with instalments unpaid (F006): cancel for non-payment with the grace rule's reason.</summary>
    CancelForNonPayment,
}

/// <summary>The day's exposure snapshot (general.exposure_snapshot), for reinsurance aggregation.</summary>
public sealed record RefreshExposureSnapshot(DateOnly Day) : ICommand<int>;

public sealed record RunDue(Guid Id, DueAction Action, DateOnly Day) : ICommand<bool>;

internal sealed class FindDueWorkHandler(IGeneralRepository general, IGeneralCodes codes) : IQueryHandler<FindDueWork, DueWork>
{
    public async Task<BuildingBlocks.Domain.Result<DueWork>> Handle(FindDueWork query, CancellationToken cancellationToken)
    {
        var invite = new List<Guid>();
        var renew = new List<Guid>();
        var remind = new List<RenewalReminder>();
        var review = new List<Guid>();
        var settings = await codes.RenewalSettings(query.Day, cancellationToken);
        foreach (var rule in await codes.RenewalRules(query.Day, cancellationToken))
        {
            // Wave 4 §4.2: a reviewed product's renewal is reviewed before its invitation, which may need the longer notice of changed terms.
            var notice = rule.Review ? Math.Max(rule.NoticeDays, settings.ChangedTermsNoticeDays) : rule.NoticeDays;
            if (rule.Review && await codes.RenewalReviewRule(rule.ProductCode, query.Day, cancellationToken) is { } reviewRule)
            {
                review.AddRange(await general.PoliciesToReview(rule.ProductCode, query.Day.AddDays(notice + reviewRule.ReviewDaysBeforeInvitation), query.Limit,
                    cancellationToken));
            }

            invite.AddRange(await general.PoliciesToInvite(rule.ProductCode, query.Day.AddDays(notice), query.Limit, cancellationToken));
            foreach (var days in (rule.ReminderDays ?? []).Where(d => d > 0).Distinct())
            {
                remind.AddRange((await general.PoliciesToRemind(rule.ProductCode, query.Day.AddDays(days), query.Limit, cancellationToken))
                    .Select(id => new RenewalReminder(id, days)));
            }

            // OQ-130: AUTOMATIC renews as the rule's auto_renew says; ON_ACCEPTANCE and ON_PAYMENT renew once accepted, or to be paid.
            if ((rule.AutoRenew || rule.Acceptance != Domain.Renewals.RenewalAcceptance.Automatic) && (!rule.ReRating || rule.Review))
            {
                renew.AddRange(await general.PoliciesToRenew(rule.ProductCode, query.Day, query.Limit, cancellationToken));
            }
        }

        return new DueWork(await general.PoliciesToStart(query.Day, query.Limit, cancellationToken), await general.PoliciesToExpire(query.Day, query.Limit, cancellationToken),
            await general.QuotesToExpire(query.Day, query.Limit, cancellationToken), invite, renew, remind,
            await general.PoliciesPastGrace(query.Day, query.Limit, cancellationToken), review);
    }
}

/// <summary>
/// The day's exposure snapshot, then the F062 §1 alert: a zone and peril at or above its alert level (the limit's own
/// alert_at_pct, else General:Aggregation:AlertAt, 80%) is recorded as an aggregation alert (GAP-051) and raises
/// AggregationLimitApproached and a warning, once for the day.
/// </summary>
internal sealed class RefreshExposureSnapshotHandler(IGeneralRepository general, IGeneralCodes codes, IOptions<AggregationOptions> options, IClock clock,
    ILogger<RefreshExposureSnapshotHandler> logger) : ICommandHandler<RefreshExposureSnapshot, int>
{
    public async Task<BuildingBlocks.Domain.Result<int>> Handle(RefreshExposureSnapshot command, CancellationToken cancellationToken)
    {
        var rows = await general.RefreshExposure(command.Day, clock.UtcNow, cancellationToken);
        var limits = await codes.ZoneLimits(command.Day, cancellationToken);
        foreach (var (zone, peril, sumInsured, currency) in await general.Snapshot(command.Day, cancellationToken))
        {
            if (limits.FirstOrDefault(l => l.Zone == zone && l.Peril == peril && l.Currency == currency) is { } limit
                && (limit.AlertAtPct / 100m ?? options.Value.AlertAt) is { } alertAt && sumInsured >= alertAt * limit.MaxExposure)
            {
                var alert = AggregationAlertView.Raise(Guid.CreateVersion7(), zone, peril, currency, sumInsured, limit.MaxExposure, alertAt, command.Day, clock.UtcNow);
                if (await general.RecordLimitApproached(alert, (limit.AlertRole ?? options.Value.AlertRole) is { Length: > 0 } role ? role : null, cancellationToken))
                {
                    logger.LogWarning("Aggregation limit approached: {Zone}/{Peril} at {Exposure} of {Limit} {Currency}", zone, peril, sumInsured, limit.MaxExposure, currency);
                }
            }
        }

        return rows;
    }
}

/// <summary>Transitions "Period starts", "Period ends without renewal" and "Validity ends".</summary>
internal sealed class RunDueHandler(IGeneralRepository general, IGeneralCodes codes, IClock clock) : ICommandHandler<RunDue, bool>
{
    public async Task<BuildingBlocks.Domain.Result<bool>> Handle(RunDue command, CancellationToken cancellationToken)
    {
        BuildingBlocks.Domain.Result result;
        switch (command.Action)
        {
            case DueAction.ExpireQuote:
                if (await general.Quote(command.Id, cancellationToken) is not { } quote)
                {
                    return false;
                }

                result = quote.ExpireQuote(command.Day, clock.UtcNow);
                break;
            default:
                if (await general.Policy(command.Id, cancellationToken) is not { } policy)
                {
                    return false;
                }

                if (command.Action == DueAction.CancelForNonPayment)
                {
                    // F006: grace ended unpaid. The grace rule names the reason, and its refund rule applies.
                    result = await codes.GraceTerms(policy.ProductCode, policy.BillingFrequency ?? "", command.Day, cancellationToken) is { } grace
                             && await codes.CancellationRule(grace.CancellationReason, command.Day, cancellationToken) is { } cancellation
                        ? policy.CancelForNonPayment(cancellation, grace, command.Day, clock.UtcNow)
                        : BuildingBlocks.Domain.Error.Validation(Domain.GeneralErrorCodes.CodeNotInForce,
                            $"{policy.ProductCode} has no grace rule, or its cancellation reason, in force for {policy.BillingFrequency}.");

                    // Wave 4 §4.4, ON_PAYMENT: a renewal whose first premium was not paid within grace lapses.
                    if (result.IsSuccess && await general.RenewalIssuedAs(policy.Id, cancellationToken) is { } unpaid)
                    {
                        unpaid.LapsedUnpaid(clock.UtcNow);
                    }

                    break;
                }

                if (command.Action == DueAction.InviteRenewal)
                {
                    if (await codes.RenewalRule(policy.ProductCode, command.Day, cancellationToken) is not { } rule)
                    {
                        result = BuildingBlocks.Domain.Error.Validation(Domain.GeneralErrorCodes.CodeNotInForce, $"{policy.ProductCode} has no renewal rule in force.");
                        break;
                    }

                    var settings = await codes.RenewalSettings(command.Day, cancellationToken);
                    var existing = await general.RenewalOfPeriod(policy.Id, policy.PeriodEnd, cancellationToken);
                    if (!rule.Review)
                    {
                        // Wave 4 §4.4: a renewal that is not reviewed is offered at its current terms when invited, so the customer's answer
                        // (or the insurer's non-renewal, which stops the invitation) has a record. Its acceptance is the product's (OQ-130).
                        if (existing is { Status: not Domain.Renewals.RenewalStatus.Offered })
                        {
                            return false;
                        }

                        var current = existing ?? Domain.Renewals.Renewal.OpenOffered(Guid.CreateVersion7(), policy.Id, policy.PolicyNumber, policy.ProductCode,
                            policy.PeriodEnd, policy.AnnualPremium, policy.SumInsuredOn(policy.PeriodEnd), policy.Currency, command.Day, clock.UtcNow);
                        current.SetAcceptance(rule.Acceptance, settings.WorseTermsPct);
                        if (existing is null)
                        {
                            general.Add(current);
                        }

                        result = policy.InviteRenewal(rule.NoticeDays, rule.InvitationTemplate, command.Day, clock.UtcNow);
                        break;
                    }

                    // Wave 4 §4.2 and addition 2: a reviewed renewal is invited once offered, with its terms; changed terms at least the
                    // tenant's notice before the renewal date, unchanged ones the rule's.
                    if (existing is not { Status: Domain.Renewals.RenewalStatus.Offered } offered)
                    {
                        return false;
                    }

                    // OQ-130: worse terms than the tenant's threshold need the customer's acceptance, whatever the product says.
                    offered.SetAcceptance(rule.Acceptance, settings.WorseTermsPct);

                    var notice = offered.ChangedTerms ? Math.Max(rule.NoticeDays, settings.ChangedTermsNoticeDays) : rule.NoticeDays;
                    if (command.Day < policy.PeriodEnd.AddDays(-notice))
                    {
                        return false;
                    }

                    result = policy.InviteRenewal(notice, rule.InvitationTemplate, command.Day, clock.UtcNow, offered.OfferedPremium);
                    break;
                }

                if (command.Action == DueAction.StartPeriod)
                {
                    result = policy.StartPeriod(command.Day);
                    break;
                }

                // Wave 4 §4.3, §4.4: why it was not renewed. A renewal still awaiting the customer's acceptance lapses now.
                var renewal = await general.LatestRenewal(policy.Id, cancellationToken);
                var reason = renewal is null ? null
                    : renewal.Lapse(clock.UtcNow) || renewal.Status == Domain.Renewals.RenewalStatus.Lapsed ? Domain.Policies.PolicyExpiryReasons.Lapsed
                    : renewal.Status switch
                    {
                        Domain.Renewals.RenewalStatus.NotRenewedByInsurer => Domain.Policies.PolicyExpiryReasons.NotRenewedByInsurer,
                        Domain.Renewals.RenewalStatus.DeclinedByCustomer => Domain.Policies.PolicyExpiryReasons.DeclinedByCustomer,
                        _ => null,
                    };
                result = policy.ExpirePolicy(command.Day, reason);
                break;
        }

        return result.IsFailure ? result.Error : true;
    }
}
