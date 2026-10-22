using Insys.BuildingBlocks.Domain;
using Insys.Health.Domain.Adjudication;

namespace Insys.Health.Domain.Members;

/// <summary>States from domain-model.json (health › HealthScheme).</summary>
public enum SchemeStatus
{
    Registered,
    Active,
    Terminated,
}

public enum SchemeTrigger
{
    StartDateReached,
    RenewScheme,
    TerminateScheme,
}

/// <summary>States from domain-model.json (health › HealthMembership).</summary>
public enum MembershipStatus
{
    Pending,
    Active,
    Suspended,
    Terminated,
}

public enum MembershipTrigger
{
    StartDateReached,
    SuspendMembership,
    ReinstateMembership,
    TerminateMembership,
}

/// <summary>Value object PlanOption: a plan and option (health.cfg_plans_and_options) and the version enrolled on.</summary>
public sealed record PlanOption(string PlanCode, string OptionCode, int PlanVersion);

/// <summary>How contributions are billed: the payer, the account and the monthly contribution (health.cfg_contribution_rules), when known.</summary>
public sealed record ContributionBilling(Guid PayerPartyId, Guid BillingAccountRef, string CollectionMethod, ContributionRate? Rate);

/// <summary>
/// A scheme category's monthly contribution per member (health.cfg_contribution_rules): the employer's share (a
/// percentage, billed to the scheme's sponsor) and the rest (billed to the payer). The employer's part per member is
/// rounded half away from zero to cents; the member's part is the remainder (Phase 5 review, item 5).
/// </summary>
public sealed record ContributionRate(decimal PerMember, decimal EmployerSharePct, Guid? SponsorPartyId)
{
    public decimal EmployerPerMember => Math.Round(PerMember * EmployerSharePct / 100m, 2, MidpointRounding.AwayFromZero);

    public decimal MemberPerMember => PerMember - EmployerPerMember;
}

/// <summary>A dependant type's age limit (health.cfg_dependant_types_and_age_limits).</summary>
public sealed record DependantRule(string TypeCode, int MaxAge, int? StudentExtensionAge);

public sealed record HealthSchemeRenewed(Guid SchemeId, string SchemeNumber, DateOnly RenewalDate, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record HealthMemberEnrolled(Guid MembershipId, string MemberNumber, Guid PrincipalPartyId, Guid? SchemeId, string PlanCode, string OptionCode,
    DateOnly StartDate, Guid PayerPartyId, Guid BillingAccountRef, string CollectionMethod, string Currency, decimal? MonthlyContribution, decimal? EmployerContribution,
    Guid? SponsorPartyId, DateTimeOffset OccurredAt)
    : IDomainEvent;

/// <summary>The membership's status or its members changed; <paramref name="MonthlyContribution"/> is the contribution from <paramref name="EffectiveDate"/>.</summary>
public sealed record HealthMemberStatusChanged(Guid MembershipId, string MemberNumber, string Status, DateOnly EffectiveDate, decimal? MonthlyContribution,
    decimal? EmployerContribution, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record DependantAdded(Guid MembershipId, string MemberNumber, Guid DependantId, Guid PartyId, string Suffix, DateOnly StartDate, decimal? MonthlyContribution,
    decimal? EmployerContribution, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// The HealthScheme aggregate (health.scheme): a sponsor's scheme. Its member categories, each mapped to a plan, are
/// static data (health.cfg_scheme_member_categories). TODO(spec): the scheme term on renewal is one year here (OQ-061).
/// </summary>
public sealed class HealthScheme : AggregateRoot<Guid>
{
    public static readonly StateMachine<SchemeStatus, SchemeTrigger> Lifecycle = new StateMachine<SchemeStatus, SchemeTrigger>()
        .Allow(SchemeStatus.Registered, SchemeTrigger.StartDateReached, SchemeStatus.Active)
        .Allow(SchemeStatus.Active, SchemeTrigger.RenewScheme, SchemeStatus.Active)
        .Allow(SchemeStatus.Active, SchemeTrigger.TerminateScheme, SchemeStatus.Terminated);

    private HealthScheme()
    {
    }

    public string SchemeNumber { get; private set; } = "";

    public string SponsorCode { get; private set; } = "";

    public SchemeStatus Status { get; private set; }

    public DateOnly StartDate { get; private set; }

    public DateOnly RenewalDate { get; private set; }

    public string? Currency { get; private set; }

    public static Result<HealthScheme> RegisterScheme(Guid id, string schemeNumber, string sponsorCode, DateOnly startDate, DateOnly renewalDate, string currency,
        DateOnly today)
    {
        if (renewalDate <= startDate)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "The renewal date is after the start date.");
        }

        var scheme = new HealthScheme
        {
            Id = id, SchemeNumber = Guard.MaxLength(schemeNumber, 12), SponsorCode = Guard.MaxLength(sponsorCode, 15), Status = SchemeStatus.Registered,
            StartDate = startDate, RenewalDate = renewalDate, Currency = currency,
        };
        if (startDate <= today)
        {
            scheme.Status = SchemeStatus.Active;
        }

        scheme.MarkChanged();
        return scheme;
    }

    public Result Activate(DateOnly today) =>
        Status == SchemeStatus.Registered && StartDate <= today ? Fire(SchemeTrigger.StartDateReached)
            : Error.Conflict(ErrorCodes.InvalidStateTransition, $"Scheme {SchemeNumber} is {Status}, from {StartDate:yyyy-MM-dd}.");

    public Result RenewScheme(DateTimeOffset now)
    {
        var result = Fire(SchemeTrigger.RenewScheme);
        if (result.IsSuccess)
        {
            RenewalDate = RenewalDate.AddYears(1);
            Raise(new HealthSchemeRenewed(Id, SchemeNumber, RenewalDate, now));
        }

        return result;
    }

    public Result TerminateScheme() => Fire(SchemeTrigger.TerminateScheme);

    private Result Fire(SchemeTrigger trigger)
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

/// <summary>Entity Dependant (health.member_dependant): a person covered under the principal's membership, with a suffix.</summary>
public sealed class Dependant : Entity<Guid>, ITenantScoped
{
    public const string ActiveStatus = "ACTIVE";
    public const string Removed = "REMOVED";

    private Dependant()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid MembershipId { get; private set; }

    public Guid PartyId { get; private set; }

    public string DependantType { get; private set; } = "";

    public string Suffix { get; private set; } = "";

    public DateOnly StartDate { get; private set; }

    public DateOnly? EndDate { get; private set; }

    public string Status { get; private set; } = ActiveStatus;

    public long RowVersion { get; private set; }

    public bool CoveredOn(DateOnly day) => StartDate <= day && (EndDate is null || day <= EndDate);

    internal static Dependant Create(Guid membershipId, Guid partyId, string type, string suffix, DateOnly start) => new()
    {
        Id = Guid.CreateVersion7(), MembershipId = membershipId, PartyId = partyId, DependantType = Guard.MaxLength(type, 10), Suffix = suffix, StartDate = start,
    };

    internal void Remove(DateOnly lastDay)
    {
        Status = Removed;
        EndDate = lastDay;
    }
}

/// <summary>Entity MembershipEvent (health.membership_event): lifecycle history. Insert-only.</summary>
public sealed class MembershipEvent : Entity<Guid>, ITenantScoped
{
    private MembershipEvent()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid MembershipId { get; private set; }

    public string EventType { get; private set; } = "";

    public DateOnly EffectiveDate { get; private set; }

    public string Reason { get; private set; } = "";

    internal static MembershipEvent Create(Guid membershipId, string eventType, DateOnly effectiveDate, string reason) => new()
    {
        Id = Guid.CreateVersion7(), MembershipId = membershipId, EventType = Guard.MaxLength(eventType, 15), EffectiveDate = effectiveDate,
        Reason = Guard.MaxLength(reason, 30),
    };
}

/// <summary>
/// The HealthMembership aggregate (health.membership, member_dependant, membership_event): a principal and dependants
/// on one plan option at a time (HLT-002). Dependants stay within their type's age limit; waiting periods are fixed at
/// enrolment. A scheme member's contribution is split by the category's employer share: the employer's part is billed
/// to the scheme's sponsor, the rest to the payer named at enrolment.
/// </summary>
public sealed class HealthMembership : AggregateRoot<Guid>
{
    public const string PrincipalSuffix = "00";

    public static readonly StateMachine<MembershipStatus, MembershipTrigger> Lifecycle = new StateMachine<MembershipStatus, MembershipTrigger>()
        .Allow(MembershipStatus.Pending, MembershipTrigger.StartDateReached, MembershipStatus.Active)
        .Allow(MembershipStatus.Active, MembershipTrigger.SuspendMembership, MembershipStatus.Suspended)
        .Allow(MembershipStatus.Suspended, MembershipTrigger.ReinstateMembership, MembershipStatus.Active)
        .Allow(MembershipStatus.Active, MembershipTrigger.TerminateMembership, MembershipStatus.Terminated)
        .Allow(MembershipStatus.Suspended, MembershipTrigger.TerminateMembership, MembershipStatus.Terminated);

    private readonly List<Dependant> _dependants = [];
    private readonly List<MembershipEvent> _events = [];

    private HealthMembership()
    {
    }

    public string MemberNumber { get; private set; } = "";

    public Guid PrincipalPartyId { get; private set; }

    public Guid? SchemeId { get; private set; }

    public string PlanCode { get; private set; } = "";

    public string OptionCode { get; private set; } = "";

    public int PlanVersion { get; private set; }

    public MembershipStatus Status { get; private set; }

    public DateOnly StartDate { get; private set; }

    public DateOnly? EndDate { get; private set; }

    public List<WaitingPeriod> WaitingPeriods { get; private set; } = [];

    public Guid? PayerPartyId { get; private set; }

    public Guid? BillingAccountRef { get; private set; }

    public string? MemberCategory { get; private set; }

    public string? Currency { get; private set; }

    public IReadOnlyList<Dependant> Dependants => _dependants;

    public IReadOnlyList<MembershipEvent> Events => _events;

    /// <summary>The people covered on a day: the principal, and dependants within their dates.</summary>
    public int MembersOn(DateOnly day) => 1 + _dependants.Count(d => d.CoveredOn(day));

    /// <summary>Whether a person is covered on a day: the membership active then, and the principal or a dependant whose dates include it.</summary>
    public bool Covers(Guid partyId, DateOnly day) =>
        Status == MembershipStatus.Active && StartDate <= day && (EndDate is null || day <= EndDate)
        && (partyId == PrincipalPartyId || _dependants.Any(d => d.PartyId == partyId && d.CoveredOn(day)));

    public string? SuffixOf(Guid partyId) => partyId == PrincipalPartyId ? PrincipalSuffix : _dependants.FirstOrDefault(d => d.PartyId == partyId)?.Suffix;

    public Guid? PartyFor(string suffix) => suffix == PrincipalSuffix ? PrincipalPartyId : _dependants.FirstOrDefault(d => d.Suffix == suffix)?.PartyId;

    /// <summary>Command EnrolMember: the principal on a plan option, with the waiting periods in force now, fixed from here.</summary>
    public static Result<HealthMembership> EnrolMember(Guid id, string memberNumber, Guid principalPartyId, Guid? schemeId, string? memberCategory, PlanOption plan,
        DateOnly startDate, IReadOnlyList<WaitingPeriod> waitingPeriods, ContributionBilling billing, string currency, DateOnly today, DateTimeOffset now)
    {
        var membership = new HealthMembership
        {
            Id = id, MemberNumber = Guard.MaxLength(memberNumber, 12), PrincipalPartyId = Guard.NotEmpty(principalPartyId), SchemeId = schemeId,
            MemberCategory = memberCategory, PlanCode = Guard.MaxLength(plan.PlanCode, 15), OptionCode = Guard.MaxLength(plan.OptionCode, 10),
            PlanVersion = plan.PlanVersion, Status = MembershipStatus.Pending, StartDate = startDate, WaitingPeriods = [.. waitingPeriods],
            PayerPartyId = billing.PayerPartyId, BillingAccountRef = billing.BillingAccountRef, Currency = currency,
        };
        membership._events.Add(MembershipEvent.Create(id, "ENROL", startDate, "ENROLMENT"));
        membership.Raise(new HealthMemberEnrolled(id, membership.MemberNumber, principalPartyId, schemeId, plan.PlanCode, plan.OptionCode, startDate,
            billing.PayerPartyId, billing.BillingAccountRef, billing.CollectionMethod, currency, billing.Rate?.MemberPerMember, billing.Rate?.EmployerPerMember,
            billing.Rate?.SponsorPartyId, now));
        if (startDate <= today)
        {
            membership.Fire(MembershipTrigger.StartDateReached);
        }

        return membership;
    }

    public Result Activate(DateOnly today) =>
        Status == MembershipStatus.Pending && StartDate <= today ? Fire(MembershipTrigger.StartDateReached)
            : Error.Conflict(ErrorCodes.InvalidStateTransition, $"Membership {MemberNumber} is {Status}, from {StartDate:yyyy-MM-dd}.");

    /// <summary>Command AddDependant: within the type's age limit on the start date (the student extension where it applies).</summary>
    public Result<Dependant> AddDependant(Guid partyId, DependantRule rule, DateOnly birthDate, bool student, DateOnly startDate, ContributionRate? rate,
        DateTimeOffset now)
    {
        if (Status is MembershipStatus.Terminated || startDate < StartDate)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Membership {MemberNumber} is {Status}; dependants join on or after its start.");
        }

        if (partyId == PrincipalPartyId || _dependants.Any(d => d.PartyId == partyId && d.Status == Dependant.ActiveStatus))
        {
            return Error.Conflict(ErrorCodes.DuplicateRecord, "The person is already covered by the membership.");
        }

        var age = startDate.Year - birthDate.Year - (birthDate.AddYears(startDate.Year - birthDate.Year) > startDate ? 1 : 0);
        var limit = student && rule.StudentExtensionAge is { } extended ? extended : rule.MaxAge;
        if (age > limit)
        {
            return Error.Validation(HealthErrorCodes.DependantNotEligible, $"A {rule.TypeCode} dependant is at most {limit}; this person is {age}.");
        }

        var next = _dependants.Count == 0 ? 1 : _dependants.Max(d => int.Parse(d.Suffix, System.Globalization.CultureInfo.InvariantCulture)) + 1;
        if (next > 99)
        {
            return Error.Validation(HealthErrorCodes.DependantNotEligible, "A membership has at most 99 dependants.");
        }

        var dependant = Dependant.Create(Id, partyId, rule.TypeCode, next.ToString("D2", System.Globalization.CultureInfo.InvariantCulture), startDate);
        _dependants.Add(dependant);
        _events.Add(MembershipEvent.Create(Id, "ADD_DEPENDANT", startDate, dependant.Suffix));
        Raise(new DependantAdded(Id, MemberNumber, dependant.Id, partyId, dependant.Suffix, startDate, rate?.MemberPerMember * MembersOn(startDate),
            rate?.EmployerPerMember * MembersOn(startDate), now));
        return dependant;
    }

    public Result RemoveDependant(Guid dependantId, DateOnly lastDay, ContributionRate? rate, DateTimeOffset now)
    {
        if (_dependants.SingleOrDefault(d => d.Id == dependantId) is not { Status: Dependant.ActiveStatus } dependant || lastDay < dependant.StartDate)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "A covered dependant is removed on or after joining.");
        }

        dependant.Remove(lastDay);
        _events.Add(MembershipEvent.Create(Id, "REMOVE_DEPENDANT", lastDay, dependant.Suffix));
        var from = lastDay.AddDays(1);
        Raise(new HealthMemberStatusChanged(Id, MemberNumber, Status.ToString(), from, rate?.MemberPerMember * MembersOn(from), rate?.EmployerPerMember * MembersOn(from), now));
        return Result.Success();
    }

    /// <summary>Command ChangePlan from a date: one plan option at a time, the change kept in the history.</summary>
    public Result ChangePlan(PlanOption plan, DateOnly effectiveDate, string reason)
    {
        if (Status is not (MembershipStatus.Active or MembershipStatus.Pending) || effectiveDate < StartDate)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Membership {MemberNumber} is {Status}.");
        }

        PlanCode = Guard.MaxLength(plan.PlanCode, 15);
        OptionCode = Guard.MaxLength(plan.OptionCode, 10);
        PlanVersion = plan.PlanVersion;
        _events.Add(MembershipEvent.Create(Id, "CHANGE_PLAN", effectiveDate, reason));
        MarkChanged();
        return Result.Success();
    }

    public Result SuspendMembership(DateOnly effectiveDate, string reason, DateTimeOffset now) => Change(MembershipTrigger.SuspendMembership, "SUSPEND", effectiveDate, reason, now);

    public Result ReinstateMembership(DateOnly effectiveDate, string reason, DateTimeOffset now) =>
        Change(MembershipTrigger.ReinstateMembership, "REINSTATE", effectiveDate, reason, now);

    public Result TerminateMembership(DateOnly lastDay, string reason, DateTimeOffset now)
    {
        var result = Change(MembershipTrigger.TerminateMembership, "TERMINATE", lastDay, reason, now);
        if (result.IsSuccess)
        {
            EndDate = lastDay;
        }

        return result;
    }

    private Result Change(MembershipTrigger trigger, string eventType, DateOnly effectiveDate, string reason, DateTimeOffset now)
    {
        var result = Fire(trigger);
        if (result.IsSuccess)
        {
            _events.Add(MembershipEvent.Create(Id, eventType, effectiveDate, reason));
            Raise(new HealthMemberStatusChanged(Id, MemberNumber, Status.ToString(), effectiveDate, null, null, now));
        }

        return result;
    }

    private Result Fire(MembershipTrigger trigger)
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
