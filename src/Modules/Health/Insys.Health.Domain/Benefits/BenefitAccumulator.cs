using Insys.BuildingBlocks.Domain;
using Insys.Health.Domain.Adjudication;

namespace Insys.Health.Domain.Benefits;

public sealed record BenefitLimitReached(Guid AccumulatorId, Guid MembershipId, Guid BeneficiaryPartyId, string BenefitCategory, DateOnly PeriodEnd, DateTimeOffset OccurredAt)
    : IDomainEvent;

public sealed record EligibilityDecisionRecorded(Guid DecisionId, string MemberNumber, string DependantSuffix, string? ProviderId, string ServiceType, string ResponseCode,
    DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// The BenefitAccumulator aggregate (health.benefit_accumulator): a benefit category's usage for a member (or a family
/// pool on the principal) in a benefit period. Used plus reserved never exceeds the limit (HLT-007); it changes only with
/// optimistic locking (DBA-011). A deductible and co-payments are tracked on the member's benefit-year accumulator,
/// which may have no limit (ADR-012).
/// </summary>
public sealed class BenefitAccumulator : AggregateRoot<Guid>
{
    private BenefitAccumulator()
    {
    }

    public Guid MembershipId { get; private set; }

    public Guid BeneficiaryPartyId { get; private set; }

    public string BenefitCategory { get; private set; } = "";

    public string Scope { get; private set; } = AccumulatorScope.Member;

    public DateOnly PeriodStart { get; private set; }

    public DateOnly PeriodEnd { get; private set; }

    public decimal? LimitAmount { get; private set; }

    public decimal UsedAmount { get; private set; }

    public decimal ReservedAmount { get; private set; }

    public decimal DeductibleMet { get; private set; }

    public decimal CopayTaken { get; private set; }

    public AccumulatorKey Key => new(Scope, BeneficiaryPartyId, BenefitCategory, PeriodStart, PeriodEnd);

    public AccumulatorState State => new(LimitAmount, UsedAmount, ReservedAmount, DeductibleMet, CopayTaken);

    public decimal? Remaining => LimitAmount is { } limit ? limit - UsedAmount - ReservedAmount : null;

    /// <summary>Command OpenNewPeriod.</summary>
    public static BenefitAccumulator OpenNewPeriod(Guid id, Guid membershipId, AccumulatorKey key, decimal? limit) => new()
    {
        Id = id, MembershipId = membershipId, BeneficiaryPartyId = key.BeneficiaryPartyId, BenefitCategory = Guard.MaxLength(key.Category, 15), Scope = key.Scope,
        PeriodStart = key.PeriodStart, PeriodEnd = key.PeriodEnd, LimitAmount = limit,
    };

    /// <summary>Command ReserveBenefit, for an approved preauthorisation.</summary>
    public Result ReserveBenefit(decimal amount, DateTimeOffset now)
    {
        if (amount < 0 || (Remaining is { } remaining && amount > remaining))
        {
            return Error.Conflict(HealthErrorCodes.LimitExceeded, $"{BenefitCategory} has {Remaining:0.00} left; {amount:0.00} cannot be reserved.");
        }

        ReservedAmount += amount;
        Changed(now);
        return Result.Success();
    }

    /// <summary>Command ReleaseReservation: at expiry or cancellation, or once a claim has used the authorisation.</summary>
    public void ReleaseReservation(decimal amount)
    {
        ReservedAmount -= Math.Min(Math.Max(0, amount), ReservedAmount);
        MarkChanged();
    }

    /// <summary>Command ConsumeBenefit: a claim's adjudicated change, releasing what its authorisation reserved for it.</summary>
    public Result ConsumeBenefit(AccumulatorChange change, DateTimeOffset now)
    {
        var reserved = ReservedAmount - Math.Min(change.Release, ReservedAmount);
        if (change.Consume < 0 || (LimitAmount is { } limit && UsedAmount + change.Consume + reserved > limit))
        {
            return Error.Conflict(HealthErrorCodes.LimitExceeded, $"{BenefitCategory}: {change.Consume:0.00} is more than is left.");
        }

        ReservedAmount = reserved;
        UsedAmount += change.Consume;
        DeductibleMet += change.Deductible;
        CopayTaken += change.CoPayment;
        Changed(now);
        return Result.Success();
    }

    /// <summary>A reversed claim gives back what it consumed.</summary>
    public void ReverseConsumption(decimal amount)
    {
        UsedAmount -= Math.Min(Math.Max(0, amount), UsedAmount);
        MarkChanged();
    }

    private void Changed(DateTimeOffset now)
    {
        if (Remaining == 0)
        {
            Raise(new BenefitLimitReached(Id, MembershipId, BeneficiaryPartyId, BenefitCategory, PeriodEnd, now));
        }
        else
        {
            MarkChanged();
        }
    }
}

/// <summary>The EligibilityDecision aggregate (health.eligibility_decision): an authorised member-eligibility enquiry's answer (HLT-003), immutable once recorded.</summary>
public sealed class EligibilityDecision : AggregateRoot<Guid>
{
    private EligibilityDecision()
    {
    }

    public string MemberNumber { get; private set; } = "";

    public string DependantSuffix { get; private set; } = "";

    public string? ProviderId { get; private set; }

    public string ServiceType { get; private set; } = "";

    public string ResponseCode { get; private set; } = "";

    public DateTimeOffset DecidedAt { get; private set; }

    /// <summary>Command CheckEligibility's record.</summary>
    public static EligibilityDecision Record(Guid id, string memberNumber, string suffix, string? providerId, string serviceType, string responseCode, DateTimeOffset now)
    {
        var decision = new EligibilityDecision
        {
            Id = id, MemberNumber = Guard.MaxLength(memberNumber, 12), DependantSuffix = Guard.MaxLength(suffix, 2), ProviderId = providerId,
            ServiceType = Guard.MaxLength(serviceType, 20), ResponseCode = Guard.MaxLength(responseCode, 10), DecidedAt = now,
        };
        decision.Raise(new EligibilityDecisionRecorded(id, decision.MemberNumber, suffix, providerId, serviceType, responseCode, now));
        return decision;
    }
}
