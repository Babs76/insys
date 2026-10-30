/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Health
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
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
using Insys.Documents.Contracts;
using Insys.Health.Contracts;
using Insys.Health.Domain.Adjudication;
using Insys.Payments.Contracts;
using Insys.Workflow.Contracts;
using Microsoft.Extensions.Logging;

namespace Insys.Health.Application;

/// <summary>
/// Health's reactions (process "Health care to provider payment"): an approved authorisation reserves its benefit and
/// releases it when it ends; an adjudicated claim marks the authorisations it used and opens high-cost reviews;
/// Workflow approves payment runs; a released run pays its claims; a paid remittance reconciles the run.
/// </summary>
public sealed class HealthEventPolicies(IHealthRepository health, BenefitContexts contexts, IClock clock, ILogger<HealthEventPolicies> logger) :
    IIntegrationEventHandler<PreauthorisationApproved>,
    IIntegrationEventHandler<PreauthorisationReservationsChanged>,
    IIntegrationEventHandler<MedicalClaimAdjudicated>,
    IIntegrationEventHandler<ApprovalGranted>,
    IIntegrationEventHandler<ApprovalRejected>,
    IIntegrationEventHandler<ProviderPaymentRunReleased>,
    IIntegrationEventHandler<PaymentSucceeded>,
    IIntegrationEventHandler<DocumentGenerated>
{
    /// <summary>A remittance advice was generated: it is attached to its remittance.</summary>
    public async Task Handle(DocumentGenerated generated, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (generated.BusinessRefType == HealthReferences.RemittanceRefType && await health.RunForRemittance(generated.BusinessRefId, cancellationToken) is { } run)
        {
            run.AttachRemittanceDocument(generated.BusinessRefId, generated.DocumentId);
        }
    }

    /// <summary>ReserveBenefit: each line's authorised amount on its category's accumulators.</summary>
    public Task Handle(PreauthorisationApproved approved, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Reserve(approved.MembershipId, approved.PatientPartyId, approved.ValidFrom, approved.Reservations, approved.AuthNumber, cancellationToken);

    /// <summary>ReleaseReservation on expiry, cancellation or use; or a modified line's change.</summary>
    public Task Handle(PreauthorisationReservationsChanged changed, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Reserve(changed.MembershipId, changed.PatientPartyId, changed.ValidFrom, changed.Changes, changed.AuthNumber, cancellationToken);

    public async Task Handle(MedicalClaimAdjudicated adjudicated, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        foreach (var use in adjudicated.AuthorisationUses)
        {
            if (await health.Preauthorisation(use.PreauthorisationId, cancellationToken) is { } preauth)
            {
                var recorded = preauth.RecordUse(use.LineId, use.Released, use.Consumed, clock.UtcNow);
                if (recorded.IsFailure)
                {
                    logger.LogWarning("Authorisation {AuthNumber} not marked used by claim {ClaimNumber}: {Code}", preauth.AuthNumber, adjudicated.ClaimNumber, recorded.Error.Code);
                }
            }
        }

        foreach (var line in adjudicated.HighCost)
        {
            health.Add(Domain.Care.UtilisationCase.OpenCase(Guid.CreateVersion7(), adjudicated.MembershipId, line.RuleCode, adjudicated.ClaimId, clock.UtcNow));
        }
    }

    /// <summary>
    /// The run's approval was granted: the run is approved for release. One already approved by this approval is a
    /// redelivery; one that will not take it (it changed after it was calculated: another subject, no longer calculated)
    /// parks the delivery (APPROVAL_NOT_APPLIED, ADR-022, D-5), never only logged.
    /// </summary>
    public async Task Handle(ApprovalGranted granted, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (granted.BusinessRefType != HealthApprovals.RunBusinessRefType || await health.Run(granted.BusinessRefId, cancellationToken) is not { } run
            || run.GrantedBy(granted.ApprovalId))
        {
            return;
        }

        if (run.DecideApproval(true, granted.ApprovalId, granted.CheckerId, granted.SubjectHash).IsFailure)
        {
            throw new PrerequisiteMissingException(PrerequisiteCodes.ApprovalNotApplied, $"{granted.ApprovalType}:{granted.ApprovalId}");
        }
    }

    /// <summary>
    /// The run's approval ended without being granted, whether rejected, expired or withdrawn (D-2): the run ends as
    /// Rejected and its claims are released for a new run. A run that will not take the ending (another subject, or no
    /// longer calculated) parks the delivery (ADR-022, D-5); one already ended by this approval is a redelivery.
    /// </summary>
    public async Task Handle(ApprovalRejected rejected, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (rejected.BusinessRefType != HealthApprovals.RunBusinessRefType || await health.Run(rejected.BusinessRefId, cancellationToken) is not { } run
            || run.EndedBy(rejected.ApprovalId))
        {
            return;
        }

        if (run.DecideApproval(false, rejected.ApprovalId, rejected.CheckerId, rejected.SubjectHash).IsFailure)
        {
            throw new PrerequisiteMissingException(PrerequisiteCodes.ApprovalEndingNotApplied, $"{rejected.ApprovalType}:{rejected.ApprovalId}");
        }

        foreach (var claim in await health.ClaimsInRun(run.Id, cancellationToken))
        {
            claim.LeaveRun(run.Id);
        }

        foreach (var recovery in await health.RecoveriesInRun(run.Id, cancellationToken))
        {
            recovery.ReleaseOffsets(run.Id);
            recovery.RepaymentNotReleased(run.Id);
        }
    }

    /// <summary>The run was released: its claims are paid ("Payment run released") and settled.</summary>
    public async Task Handle(ProviderPaymentRunReleased released, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        foreach (var claim in await health.ClaimsInRun(released.RunId, cancellationToken))
        {
            if (claim.Status == Domain.Care.MedicalClaimStatus.ApprovedForPayment)
            {
                var paid = claim.MarkPaid(released.RunId, released.ReleasedOn, clock.UtcNow);
                if (paid.IsFailure)
                {
                    throw new InvalidOperationException($"Claim {claim.ClaimNumber} could not be paid by run {released.RunCode}: {paid.Error.Message}");
                }
            }
        }

        // Gap 2: the run's offsets are taken, and what it repays is repaid.
        foreach (var recovery in await health.RecoveriesInRun(released.RunId, cancellationToken))
        {
            recovery.TakeOffsets(released.RunId, released.ReleasedOn, clock.UtcNow);
            recovery.RepaymentReleased(released.RunId, released.ReleasedOn, clock.UtcNow);
        }
    }

    /// <summary>A remittance's payment succeeded: the run is reconciled once all have ("Payments matched", HLT-009).</summary>
    public async Task Handle(PaymentSucceeded succeeded, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (succeeded.BusinessRefType == HealthReferences.RemittanceRefType && succeeded.BusinessRefId is { } remittanceId
            && await health.RunForRemittance(remittanceId, cancellationToken) is { } run)
        {
            run.RecordPayment(remittanceId, succeeded.PaymentId, succeeded.ValueDate);
        }
    }

    private async Task Reserve(Guid membershipId, Guid patientPartyId, DateOnly validFrom, IReadOnlyList<ReservationChange> changes, string authNumber,
        CancellationToken cancellationToken)
    {
        await health.LockBenefits(membershipId, cancellationToken);
        if (await health.Membership(membershipId, cancellationToken) is not { } membership)
        {
            return;
        }

        var categories = changes.Select(c => c.Category).Distinct().ToList();
        var context = await contexts.For(membership, patientPartyId, null, validFrom, Guid.Empty, [], categories, false, null, cancellationToken);
        if (context.IsFailure)
        {
            throw new InvalidOperationException($"Authorisation {authNumber}'s benefit could not be read: {context.Error.Message}");
        }

        foreach (var (category, amount) in changes.GroupBy(c => c.Category).Select(g => (g.Key, g.Sum(c => c.Amount))))
        {
            foreach (var accumulator in await AccumulatorsFor(membership.Id, Limits.For(category, context.Value, validFrom).Accumulators, cancellationToken))
            {
                if (amount >= 0)
                {
                    var reserved = accumulator.ReserveBenefit(amount, clock.UtcNow);
                    if (reserved.IsFailure)
                    {
                        throw new InvalidOperationException($"Authorisation {authNumber} could not reserve {category}: {reserved.Error.Message}");
                    }
                }
                else
                {
                    accumulator.ReleaseReservation(-amount);
                }
            }
        }
    }

    /// <summary>The accumulators for the keys, opened for the period where they do not exist yet.</summary>
    private async Task<IReadOnlyList<Domain.Benefits.BenefitAccumulator>> AccumulatorsFor(Guid membershipId, IReadOnlyDictionary<AccumulatorKey, decimal?> keys,
        CancellationToken cancellationToken)
    {
        var existing = (await health.Accumulators(membershipId, keys.Keys.ToList(), cancellationToken)).ToDictionary(a => a.Key);
        foreach (var (key, limit) in keys.Where(k => !existing.ContainsKey(k.Key)))
        {
            var opened = Domain.Benefits.BenefitAccumulator.OpenNewPeriod(Guid.CreateVersion7(), membershipId, key, limit);
            health.Add(opened);
            existing[key] = opened;
        }

        return existing.Values.ToList();
    }
}

public sealed record HealthDueWork(IReadOnlyList<Guid> AuthorisationsToExpire, IReadOnlyList<Guid> MembershipsToActivate, IReadOnlyList<Guid> SchemesToActivate,
    IReadOnlyList<Guid>? MembershipsToSuspend = null, IReadOnlyList<Guid>? TerminationsDue = null, IReadOnlyList<Guid>? HeldClaimsToRelease = null,
    IReadOnlyList<Guid>? SchemesToNotice = null);

public sealed record FindHealthDueWork(DateOnly Day, int Limit) : IQuery<HealthDueWork>;

/// <summary>
/// F006: claims held for arrears whose membership is suspended for them. Found after the run's suspensions, so a claim is
/// declined in the same run as its membership's suspension (addenda/renewals-and-retention.md, Health grace).
/// </summary>
public sealed record FindHeldClaimsToDecline(int Limit) : IQuery<IReadOnlyList<Guid>>;

/// <summary>Memberships with no product take their plan's, once it names one (addenda/health-plan-products.md §4).</summary>
public sealed record AssignPlanProducts(DateOnly Day) : ICommand<int>;

internal sealed class AssignPlanProductsHandler(IHealthRepository health, ProductFactory.Contracts.ISellableProducts products, ITenantContext tenant, IClock clock)
    : ICommandHandler<AssignPlanProducts, int>
{
    /// <summary>Each membership records the version of its product on sale that day (GAP-050), as at enrolment.</summary>
    public async Task<Result<int>> Handle(AssignPlanProducts command, CancellationToken cancellationToken) =>
        await health.AssignPlanProducts(command.Day,
            async (code, ct) => await products.Sellable(tenant.RequireTenantId(), code, null, command.Day, ct) is { Domain: "HEALTH" } product ? product.Version : null,
            clock.UtcNow, cancellationToken);
}

public enum HealthDueAction
{
    ExpireAuthorisation,
    ActivateMembership,
    ActivateScheme,

    /// <summary>F006: contributions still unpaid on the suspension day.</summary>
    SuspendForArrears,

    /// <summary>F006: the termination notice ran out; Workflow asks a person.</summary>
    RaiseTerminationDue,

    /// <summary>F006: a claim held for arrears, declined once the membership is suspended for them.</summary>
    DeclineHeldClaim,

    /// <summary>Wave 4 §4.5: the scheme's renewal notice to its sponsor, by its renewal rule's notice.</summary>
    NoticeSchemeRenewal,
}

public sealed record RunHealthDue(Guid Id, HealthDueAction Action, DateOnly Day) : ICommand<bool>;

internal sealed class FindHealthDueWorkHandler(IHealthRepository health) : IQueryHandler<FindHealthDueWork, HealthDueWork>
{
    public async Task<Result<HealthDueWork>> Handle(FindHealthDueWork query, CancellationToken cancellationToken)
    {
        var (release, _) = await health.HeldClaims(query.Limit, cancellationToken);
        return new HealthDueWork(await health.PreauthorisationsToExpire(query.Day, query.Limit, cancellationToken),
            await health.MembershipsToActivate(query.Day, query.Limit, cancellationToken), await health.SchemesToActivate(query.Day, query.Limit, cancellationToken),
            await health.MembershipsToSuspendForArrears(query.Day, query.Limit, cancellationToken), await health.MembershipsTerminationDue(query.Day, query.Limit, cancellationToken),
            release, await health.SchemesToNotice(query.Day, query.Limit, cancellationToken));
    }
}

internal sealed class FindHeldClaimsToDeclineHandler(IHealthRepository health) : IQueryHandler<FindHeldClaimsToDecline, IReadOnlyList<Guid>>
{
    public async Task<Result<IReadOnlyList<Guid>>> Handle(FindHeldClaimsToDecline query, CancellationToken cancellationToken) =>
        Result.Success((await health.HeldClaims(query.Limit, cancellationToken)).Decline);
}

/// <summary>Transitions "Validity ends" (HLT-005: authorisations expire automatically) and "Start date reached".</summary>
internal sealed class RunHealthDueHandler(IHealthRepository health, IHealthCodes codes, HealthDeclineRights rights, IClock clock) : ICommandHandler<RunHealthDue, bool>
{
    /// <summary>The reason a claim held for arrears is declined with once the membership is suspended for them (F006).</summary>
    public const string ArrearsDeclineReason = "CONTRIBUTIONS_ARREARS";

    public async Task<Result<bool>> Handle(RunHealthDue command, CancellationToken cancellationToken)
    {
        var result = command.Action switch
        {
            HealthDueAction.SuspendForArrears => await health.Membership(command.Id, cancellationToken) is { } member
                ? await codes.ContributionGrace(member.SchemeId is { } schemeId && await health.Scheme(schemeId, cancellationToken) is { } scheme ? scheme.SchemeNumber : null,
                    command.Day, cancellationToken) is { } grace
                    ? member.SuspendForArrears(grace, command.Day, clock.UtcNow)
                    : Error.Validation(Domain.HealthErrorCodes.CodeNotInForce, $"No contribution grace rule is in force for membership {member.MemberNumber}.")
                : Result.Success(),
            HealthDueAction.RaiseTerminationDue => await health.Membership(command.Id, cancellationToken) is { } due
                ? due.RaiseTerminationDue(command.Day, clock.UtcNow)
                : Result.Success(),
            HealthDueAction.NoticeSchemeRenewal => await health.Scheme(command.Id, cancellationToken) is { } scheme
                ? await codes.SchemeRenewalRule(scheme.SchemeNumber, command.Day, cancellationToken) is { } rule
                    && await codes.SponsorParty(scheme.SponsorCode, command.Day, cancellationToken) is { } sponsor
                    ? scheme.NoticeRenewal(rule.NoticeDays, sponsor, rule.ReRatingBasis, command.Day, clock.UtcNow)
                    : Error.Conflict(ErrorCodes.InvalidStateTransition, $"Scheme {scheme.SchemeNumber} has no renewal rule or sponsor party in force.")
                : Result.Success(),
            HealthDueAction.DeclineHeldClaim => await health.Claim(command.Id, cancellationToken) is { Status: Domain.Care.MedicalClaimStatus.Pended } claim
                ? claim.RejectClaim(ArrearsDeclineReason, clock.UtcNow) is { IsSuccess: true } ? await rights.Notice(claim, ArrearsDeclineReason, cancellationToken)
                    : Error.Conflict(ErrorCodes.InvalidStateTransition, $"Claim {claim.ClaimNumber} could not be declined.")
                : Result.Success(),
            HealthDueAction.ExpireAuthorisation => await health.Preauthorisation(command.Id, cancellationToken) is { } preauth
                ? preauth.Expire(command.Day, clock.UtcNow)
                : Result.Success(),
            HealthDueAction.ActivateMembership => await health.Membership(command.Id, cancellationToken) is { } membership ? membership.Activate(command.Day) : Result.Success(),
            _ => await health.Scheme(command.Id, cancellationToken) is { } scheme ? scheme.Activate(command.Day) : Result.Success(),
        };
        return result.IsFailure ? result.Error : true;
    }
}
