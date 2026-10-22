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

    public Task Handle(ApprovalGranted granted, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Decide(granted.BusinessRefType, granted.BusinessRefId, true, granted.ApprovalId, granted.CheckerId, granted.SubjectHash, cancellationToken);

    public Task Handle(ApprovalRejected rejected, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Decide(rejected.BusinessRefType, rejected.BusinessRefId, false, rejected.ApprovalId, rejected.CheckerId, rejected.SubjectHash, cancellationToken);

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

    private async Task Decide(string businessRefType, Guid id, bool approved, Guid approvalId, Guid checkerId, string? subjectHash, CancellationToken cancellationToken)
    {
        if (businessRefType == HealthApprovals.RunBusinessRefType && await health.Run(id, cancellationToken) is { } run)
        {
            var decided = run.DecideApproval(approved, approvalId, checkerId, subjectHash);
            if (decided.IsFailure)
            {
                logger.LogWarning("Approval {ApprovalId} not applied to run {RunCode}: {Code}", approvalId, run.RunCode, decided.Error.Code);
            }
        }
    }
}

public sealed record HealthDueWork(IReadOnlyList<Guid> AuthorisationsToExpire, IReadOnlyList<Guid> MembershipsToActivate, IReadOnlyList<Guid> SchemesToActivate);

public sealed record FindHealthDueWork(DateOnly Day, int Limit) : IQuery<HealthDueWork>;

public enum HealthDueAction
{
    ExpireAuthorisation,
    ActivateMembership,
    ActivateScheme,
}

public sealed record RunHealthDue(Guid Id, HealthDueAction Action, DateOnly Day) : ICommand<bool>;

internal sealed class FindHealthDueWorkHandler(IHealthRepository health) : IQueryHandler<FindHealthDueWork, HealthDueWork>
{
    public async Task<Result<HealthDueWork>> Handle(FindHealthDueWork query, CancellationToken cancellationToken) =>
        new HealthDueWork(await health.PreauthorisationsToExpire(query.Day, query.Limit, cancellationToken),
            await health.MembershipsToActivate(query.Day, query.Limit, cancellationToken), await health.SchemesToActivate(query.Day, query.Limit, cancellationToken));
}

/// <summary>Transitions "Validity ends" (HLT-005: authorisations expire automatically) and "Start date reached".</summary>
internal sealed class RunHealthDueHandler(IHealthRepository health, IClock clock) : ICommandHandler<RunHealthDue, bool>
{
    public async Task<Result<bool>> Handle(RunHealthDue command, CancellationToken cancellationToken)
    {
        var result = command.Action switch
        {
            HealthDueAction.ExpireAuthorisation => await health.Preauthorisation(command.Id, cancellationToken) is { } preauth
                ? preauth.Expire(command.Day, clock.UtcNow)
                : Result.Success(),
            HealthDueAction.ActivateMembership => await health.Membership(command.Id, cancellationToken) is { } membership ? membership.Activate(command.Day) : Result.Success(),
            _ => await health.Scheme(command.Id, cancellationToken) is { } scheme ? scheme.Activate(command.Day) : Result.Success(),
        };
        return result.IsFailure ? result.Error : true;
    }
}
