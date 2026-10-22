using Insys.BuildingBlocks.Domain;

namespace Insys.Health.Domain.Care;

/// <summary>States from domain-model.json (health › Preauthorisation).</summary>
public enum PreauthStatus
{
    Requested,
    InReview,
    Approved,
    ConditionallyApproved,
    Rejected,
    Utilised,
    Expired,
    Cancelled,
}

public enum PreauthTrigger
{
    ReviewClinically,
    ApproveAuthorisation,
    ApproveWithConditions,
    RejectAuthorisation,
    MatchedToClaim,
    ValidityEnds,
    CancelAuthorisation,
}

public sealed record PreauthLineRequest(string ProcedureCode, int Quantity, decimal RequestedAmount);

/// <summary>What a line authorises, in its benefit category.</summary>
public sealed record LineAuthorisation(Guid LineId, decimal AuthorisedAmount, string Category);

/// <summary>A line's reservation on its category's accumulators: reserved (positive) or released (negative).</summary>
public sealed record ReservationChange(Guid LineId, string Category, decimal Amount);

public sealed record PreauthorisationApproved(Guid PreauthorisationId, string AuthNumber, Guid MembershipId, Guid PatientPartyId, DateOnly ValidFrom, DateOnly ValidTo,
    IReadOnlyList<ReservationChange> Reservations, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record PreauthorisationExpired(Guid PreauthorisationId, string AuthNumber, DateOnly ValidTo, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record PreauthorisationRejected(Guid PreauthorisationId, string AuthNumber, string Reason, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>The authorisation ended (expired, cancelled or used) or changed: what its lines still held is released, or the change reserved.</summary>
public sealed record PreauthorisationReservationsChanged(Guid PreauthorisationId, string AuthNumber, Guid MembershipId, Guid PatientPartyId, DateOnly ValidFrom,
    IReadOnlyList<ReservationChange> Changes, string Why, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>Entity PreauthLine (health.preauth_line): a procedure, its authorised amount, and what it holds and has used on the accumulator.</summary>
public sealed class PreauthLine : Entity<Guid>, ITenantScoped
{
    private PreauthLine()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid PreauthId { get; private set; }

    public string ProcedureCode { get; private set; } = "";

    public int Quantity { get; private set; }

    public decimal AuthorisedAmount { get; private set; }

    public string? BenefitCategory { get; private set; }

    public decimal ReservedAmount { get; private set; }

    public decimal ConsumedAmount { get; private set; }

    public long RowVersion { get; private set; }

    internal static PreauthLine Create(Guid preauthId, PreauthLineRequest request) => new()
    {
        Id = Guid.CreateVersion7(), PreauthId = preauthId, ProcedureCode = Guard.MaxLength(request.ProcedureCode, 10), Quantity = request.Quantity,
        AuthorisedAmount = request.RequestedAmount,
    };

    internal void Authorise(decimal amount, string category)
    {
        AuthorisedAmount = amount;
        BenefitCategory = category;
    }

    internal void Reserve(decimal amount) => ReservedAmount += amount;

    internal decimal ReleaseAll()
    {
        var held = ReservedAmount;
        ReservedAmount = 0;
        return held;
    }

    internal void Use(decimal released, decimal consumed)
    {
        ReservedAmount -= Math.Min(released, ReservedAmount);
        ConsumedAmount += consumed;
    }
}

/// <summary>
/// The Preauthorisation aggregate (health.preauthorisation, preauth_line): requested by a provider for an eligible member
/// (HLT-005), reviewed clinically by roles with that purpose (HLT-008), approved within the available benefit, and
/// expiring at the end of its validity. While approved it holds a reservation on the benefit (ADR-012, h).
/// TODO(spec): domain-model.json gives a conditionally approved authorisation no further transitions, so it is not
/// matched to claims (OQ-061).
/// </summary>
public sealed class Preauthorisation : AggregateRoot<Guid>
{
    public static readonly StateMachine<PreauthStatus, PreauthTrigger> Lifecycle = new StateMachine<PreauthStatus, PreauthTrigger>()
        .Allow(PreauthStatus.Requested, PreauthTrigger.ReviewClinically, PreauthStatus.InReview)
        .Allow(PreauthStatus.InReview, PreauthTrigger.ApproveAuthorisation, PreauthStatus.Approved)
        .Allow(PreauthStatus.InReview, PreauthTrigger.ApproveWithConditions, PreauthStatus.ConditionallyApproved)
        .Allow(PreauthStatus.InReview, PreauthTrigger.RejectAuthorisation, PreauthStatus.Rejected)
        .Allow(PreauthStatus.Approved, PreauthTrigger.MatchedToClaim, PreauthStatus.Utilised)
        .Allow(PreauthStatus.Approved, PreauthTrigger.ValidityEnds, PreauthStatus.Expired)
        .Allow(PreauthStatus.Approved, PreauthTrigger.CancelAuthorisation, PreauthStatus.Cancelled);

    private readonly List<PreauthLine> _lines = [];

    private Preauthorisation()
    {
    }

    public string AuthNumber { get; private set; } = "";

    public Guid MembershipId { get; private set; }

    public Guid PatientPartyId { get; private set; }

    public string ProviderId { get; private set; } = "";

    public string[] DiagnosisCodes { get; private set; } = [];

    public decimal RequestedAmount { get; private set; }

    public decimal? AuthorisedAmount { get; private set; }

    public PreauthStatus Status { get; private set; }

    public DateOnly? ValidFrom { get; private set; }

    public DateOnly? ValidTo { get; private set; }

    public Guid? ClinicalReviewer { get; private set; }

    public string? ServiceType { get; private set; }

    public string? Conditions { get; private set; }

    public Guid? DecidedBy { get; private set; }

    public string? DecisionReason { get; private set; }

    public IReadOnlyList<PreauthLine> Lines => _lines;

    public bool ValidOn(DateOnly day) => Status == PreauthStatus.Approved && ValidFrom <= day && day <= ValidTo;

    /// <summary>Command RequestAuthorisation: the application checks the member is covered on the service date.</summary>
    public static Result<Preauthorisation> RequestAuthorisation(Guid id, string authNumber, Guid membershipId, Guid patientPartyId, string providerId,
        IReadOnlyList<string> diagnosisCodes, string serviceType, IReadOnlyList<PreauthLineRequest> lines)
    {
        if (lines.Count == 0 || lines.Any(l => l.Quantity <= 0 || l.RequestedAmount <= 0))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "An authorisation has lines, each with a quantity and an amount above zero.");
        }

        var preauth = new Preauthorisation
        {
            Id = id, AuthNumber = Guard.MaxLength(authNumber, 15), MembershipId = membershipId, PatientPartyId = patientPartyId, ProviderId = Guard.MaxLength(providerId, 15),
            DiagnosisCodes = [.. diagnosisCodes], ServiceType = serviceType, RequestedAmount = lines.Sum(l => l.RequestedAmount), Status = PreauthStatus.Requested,
        };
        preauth._lines.AddRange(lines.Select(l => PreauthLine.Create(id, l)));
        preauth.MarkChanged();
        return preauth;
    }

    /// <summary>Command ReviewClinically: by a clinical reviewer (HLT-008, checked by permission).</summary>
    public Result ReviewClinically(Guid reviewer)
    {
        var result = Fire(PreauthTrigger.ReviewClinically);
        if (result.IsSuccess)
        {
            ClinicalReviewer = reviewer;
        }

        return result;
    }

    /// <summary>
    /// Command ApproveAuthorisation (or ApproveWithConditions): each line's authorised amount, at most what was requested,
    /// and its benefit category; valid from a date for the service type's days (health.cfg_authorisation_validity_periods).
    /// </summary>
    public Result Approve(IReadOnlyList<LineAuthorisation> authorisations, DateOnly validFrom, int daysValid, string? conditions, Guid by, DateTimeOffset now)
    {
        if (authorisations.Count != _lines.Count || authorisations.Any(a => _lines.SingleOrDefault(l => l.Id == a.LineId) is not { } line
                                                                          || a.AuthorisedAmount < 0 || a.AuthorisedAmount > line.AuthorisedAmount))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "Each line is authorised once, for at most the amount requested.");
        }

        var result = Fire(conditions is null ? PreauthTrigger.ApproveAuthorisation : PreauthTrigger.ApproveWithConditions);
        if (result.IsFailure)
        {
            return result;
        }

        foreach (var authorisation in authorisations)
        {
            var line = _lines.Single(l => l.Id == authorisation.LineId);
            line.Authorise(authorisation.AuthorisedAmount, authorisation.Category);
            if (Status == PreauthStatus.Approved)
            {
                line.Reserve(authorisation.AuthorisedAmount);
            }
        }

        AuthorisedAmount = authorisations.Sum(a => a.AuthorisedAmount);
        ValidFrom = validFrom;
        ValidTo = validFrom.AddDays(daysValid - 1);
        Conditions = conditions;
        DecidedBy = by;
        if (Status == PreauthStatus.Approved)
        {
            Raise(new PreauthorisationApproved(Id, AuthNumber, MembershipId, PatientPartyId, validFrom, ValidTo.Value,
                _lines.Select(l => new ReservationChange(l.Id, l.BenefitCategory!, l.ReservedAmount)).ToList(), now));
        }

        return Result.Success();
    }

    public Result RejectAuthorisation(string reason, Guid by, DateTimeOffset now)
    {
        var result = Fire(PreauthTrigger.RejectAuthorisation);
        if (result.IsSuccess)
        {
            DecisionReason = Guard.MaxLength(reason, 30);
            DecidedBy = by;
            Raise(new PreauthorisationRejected(Id, AuthNumber, DecisionReason, now));
        }

        return result;
    }

    /// <summary>Command ModifyAuthorisation: an approved line's amount changes, and its reservation with it.</summary>
    public Result ModifyAuthorisation(Guid lineId, decimal authorisedAmount, DateTimeOffset now)
    {
        if (Status != PreauthStatus.Approved || _lines.SingleOrDefault(l => l.Id == lineId) is not { } line || authorisedAmount < line.ConsumedAmount)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "Only an approved line is modified, to at least what claims have used.");
        }

        var delta = authorisedAmount - line.AuthorisedAmount;
        line.Authorise(authorisedAmount, line.BenefitCategory!);
        line.Reserve(Math.Max(delta, -line.ReservedAmount));
        AuthorisedAmount = _lines.Sum(l => l.AuthorisedAmount);
        Raise(new PreauthorisationReservationsChanged(Id, AuthNumber, MembershipId, PatientPartyId, ValidFrom!.Value,
            [new ReservationChange(lineId, line.BenefitCategory!, delta)], "MODIFIED", now));
        return Result.Success();
    }

    public Result CancelAuthorisation(DateTimeOffset now) => End(PreauthTrigger.CancelAuthorisation, "CANCELLED", now);

    /// <summary>Transition "Validity ends".</summary>
    public Result Expire(DateOnly today, DateTimeOffset now) =>
        Status == PreauthStatus.Approved && ValidTo < today ? End(PreauthTrigger.ValidityEnds, "EXPIRED", now)
            : Error.Conflict(ErrorCodes.InvalidStateTransition, $"Authorisation {AuthNumber} is {Status}, valid to {ValidTo:yyyy-MM-dd}.");

    /// <summary>Transition "Matched to a claim": the claim released and used part of a line; what the authorisation still holds is released.</summary>
    public Result RecordUse(Guid lineId, decimal released, decimal consumed, DateTimeOffset now)
    {
        if (Status == PreauthStatus.Utilised)
        {
            _lines.SingleOrDefault(l => l.Id == lineId)?.Use(0, consumed);
            MarkChanged();
            return Result.Success();
        }

        if (_lines.SingleOrDefault(l => l.Id == lineId) is not { } line)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The authorisation has no such line.");
        }

        line.Use(released, consumed);
        return End(PreauthTrigger.MatchedToClaim, "UTILISED", now);
    }

    private Result End(PreauthTrigger trigger, string why, DateTimeOffset now)
    {
        var result = Fire(trigger);
        if (result.IsSuccess)
        {
            var released = _lines.Select(l => (Line: l, Held: l.ReleaseAll())).Where(x => x.Held > 0)
                .Select(x => new ReservationChange(x.Line.Id, x.Line.BenefitCategory!, -x.Held)).ToList();
            if (trigger == PreauthTrigger.ValidityEnds)
            {
                Raise(new PreauthorisationExpired(Id, AuthNumber, ValidTo!.Value, now));
            }

            if (released.Count > 0)
            {
                Raise(new PreauthorisationReservationsChanged(Id, AuthNumber, MembershipId, PatientPartyId, ValidFrom!.Value, released, why, now));
            }
        }

        return result;
    }

    private Result Fire(PreauthTrigger trigger)
    {
        var next = Lifecycle.Fire(Status, trigger);
        if (next.IsFailure)
        {
            return next.Error;
        }

        Status = next.Value;
        MarkChanged();
        return Result.Success();
    }
}
