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
public sealed class GeneralPolicies(IGeneralRepository general, IGeneralCodes codes, ISellableProducts products, ITenantContext tenant, IBusinessDate businessDate, IClock clock,
    ILogger<GeneralPolicies> logger) :
    IIntegrationEventHandler<PremiumCalculated>,
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

    /// <summary>ApprovalGranted → ApproveEndorsement or ApproveSettlement.</summary>
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

        var assessment = LossAssessment.AssignAssessor(Guid.CreateVersion7(), appointed.ClaimId, appointed.AssessorPartyId, onApprovedPanel: true);
        general.Add(assessment.Value);
    }

    private async Task Decide(string businessRefType, Guid id, bool approved, Guid approvalId, Guid checkerId, string? subjectHash, CancellationToken cancellationToken)
    {
        if (businessRefType == GeneralApprovals.EndorsementBusinessRefType && await general.Endorsement(id, cancellationToken) is { } endorsement)
        {
            endorsement.Decide(approved, approvalId, checkerId, clock.UtcNow);
        }
        else if (businessRefType == GeneralApprovals.SettlementBusinessRefType && await general.Claim(id, cancellationToken) is { } claim)
        {
            var decided = claim.DecideSettlement(approved, approvalId, checkerId, subjectHash, clock.UtcNow);
            if (decided.IsFailure)
            {
                logger.LogWarning("Approval {ApprovalId} not applied to claim {ClaimId}: {Code}", approvalId, id, decided.Error.Code);
            }
        }
    }
}

public sealed record DueWork(IReadOnlyList<Guid> PoliciesToStart, IReadOnlyList<Guid> PoliciesToExpire, IReadOnlyList<Guid> QuotesToExpire,
    IReadOnlyList<Guid> PoliciesToInvite, IReadOnlyList<Guid> PoliciesToRenew, IReadOnlyList<RenewalReminder>? RenewalReminders = null);

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
        foreach (var rule in await codes.RenewalRules(query.Day, cancellationToken))
        {
            invite.AddRange(await general.PoliciesToInvite(rule.ProductCode, query.Day.AddDays(rule.NoticeDays), query.Limit, cancellationToken));
            foreach (var days in (rule.ReminderDays ?? []).Where(d => d > 0).Distinct())
            {
                remind.AddRange((await general.PoliciesToRemind(rule.ProductCode, query.Day.AddDays(days), query.Limit, cancellationToken))
                    .Select(id => new RenewalReminder(id, days)));
            }

            if (rule.AutoRenew && !rule.ReRating)
            {
                renew.AddRange(await general.PoliciesToRenew(rule.ProductCode, query.Day, query.Limit, cancellationToken));
            }
        }

        return new DueWork(await general.PoliciesToStart(query.Day, query.Limit, cancellationToken), await general.PoliciesToExpire(query.Day, query.Limit, cancellationToken),
            await general.QuotesToExpire(query.Day, query.Limit, cancellationToken), invite, renew, remind);
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

                if (command.Action == DueAction.InviteRenewal)
                {
                    result = await codes.RenewalRule(policy.ProductCode, command.Day, cancellationToken) is { } rule
                        ? policy.InviteRenewal(rule.NoticeDays, rule.InvitationTemplate, command.Day, clock.UtcNow)
                        : BuildingBlocks.Domain.Error.Validation(Domain.GeneralErrorCodes.CodeNotInForce, $"{policy.ProductCode} has no renewal rule in force.");
                    break;
                }

                result = command.Action == DueAction.StartPeriod ? policy.StartPeriod(command.Day) : policy.ExpirePolicy(command.Day);
                break;
        }

        return result.IsFailure ? result.Error : true;
    }
}
