using System.Globalization;
using System.Text.RegularExpressions;
using Insys.BuildingBlocks.Domain;
using Insys.Life.Domain.Proposals;
using Insys.Life.Domain.Underwriting;

namespace Insys.Life.Domain.Policies;

/// <summary>States from domain-model.json (life › LifePolicy).</summary>
public enum LifePolicyStatus
{
    Issued,
    InForce,
    Lapsed,
    Terminated,
    PaidUp,
    Surrendered,
    Claimed,
    Matured,
}

public enum LifePolicyTrigger
{
    InceptionDateReached,
    LapsePolicy,
    ReinstatePolicy,
    ReinstatementWindowPassed,
    MakePaidUp,
    SurrenderPolicy,
    CloseOnClaim,
    MaturePolicy,
}

/// <summary>How the policy is billed: the account, payer, premium frequency and collection method.</summary>
public sealed record LifeBillingArrangement(Guid BillingAccountRef, Guid PayerPartyId, string Frequency, string CollectionMethod);

/// <summary>A frequency's grace period (life.cfg_grace_period_rules).</summary>
public sealed record GraceRule(int GraceDays, bool CoverDuringGrace);

/// <summary>A product's lapse and reinstatement rule (life.cfg_lapse_and_reinstatement_rules, LIF-005).</summary>
public sealed record LapseRule(int MissedPaymentsToLapse, string ReinstatementWindow, string EvidenceRequired);

/// <summary>An alteration type (life.cfg_alteration_types): the policy states it is allowed in, and whether it needs re-underwriting.</summary>
public sealed record AlterationType(string Code, IReadOnlyList<string> AllowedStates, bool ReUnderwriting, string AuthorityRole, string DocumentType);

public sealed record LifePolicyIssued(Guid PolicyId, string PolicyNumber, Guid ProposalId, string ProductCode, int ProductVersion, int RatingVersion,
    Guid PolicyholderPartyId, LifeBillingArrangement Billing, string Currency, decimal Premium, DateOnly InceptionDate, DateOnly? TermEndDate, decimal SumAssured,
    DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record LifePolicyChanged(Guid PolicyId, string PolicyNumber, string ChangeType, DateOnly EffectiveDate, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record LifePolicyLapsed(Guid PolicyId, string PolicyNumber, DateOnly LapseDate, int MissedPayments, string ProductCode, int ProductVersion, string Currency,
    DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record LifePolicyReinstated(Guid PolicyId, string PolicyNumber, DateOnly ReinstatedOn, DateOnly? TermEndDate, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>ISO 8601 periods such as P6M, P1Y or P90D, as the static data writes durations.</summary>
public static partial class IsoPeriod
{
    [GeneratedRegex(@"^P(?:(?<y>\d+)Y)?(?:(?<m>\d+)M)?(?:(?<w>\d+)W)?(?:(?<d>\d+)D)?$")]
    private static partial Regex Pattern();

    public static DateOnly? Add(DateOnly day, string period)
    {
        var match = Pattern().Match(period.Trim().ToUpperInvariant());
        if (!match.Success || period.Trim().Length < 3)
        {
            return null;
        }

        int Part(string name) => match.Groups[name].Success ? int.Parse(match.Groups[name].Value, CultureInfo.InvariantCulture) : 0;
        return day.AddYears(Part("y")).AddMonths(Part("m")).AddDays(Part("w") * 7 + Part("d"));
    }
}

/// <summary>Entity InsuredLife (life.policy_insured_life).</summary>
public sealed class PolicyInsuredLife : Entity<Guid>, ITenantScoped
{
    private PolicyInsuredLife()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid PolicyId { get; private set; }

    public Guid PartyId { get; private set; }

    public string LifeRole { get; private set; } = "";

    public int EntryAge { get; private set; }

    public long RowVersion { get; private set; }

    internal static PolicyInsuredLife Create(Guid policyId, ProposalInsuredLife life) => new()
    {
        Id = Guid.CreateVersion7(), PolicyId = policyId, PartyId = life.PartyId, LifeRole = life.LifeRole, EntryAge = life.AgeNext,
    };
}

/// <summary>Entity LifeBenefit (life.policy_benefit): a benefit on an insured life, from its start to its end date (both included).</summary>
public sealed class LifeBenefit : Entity<Guid>, ITenantScoped
{
    public const string Active = "ACTIVE";
    public const string ClaimedStatus = "CLAIMED";

    private LifeBenefit()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid PolicyId { get; private set; }

    public Guid? InsuredLifeId { get; private set; }

    public string BenefitCode { get; private set; } = "";

    public decimal SumAssured { get; private set; }

    public decimal Premium { get; private set; }

    public decimal? LoadingPct { get; private set; }

    public string[]? ExclusionCodes { get; private set; }

    public DateOnly StartDate { get; private set; }

    public DateOnly? EndDate { get; private set; }

    public string Status { get; private set; } = Active;

    public long RowVersion { get; private set; }

    public bool CoversOn(DateOnly day) => Status == Active && StartDate <= day && (EndDate is null || day <= EndDate);

    internal static LifeBenefit Create(Guid policyId, Guid insuredLifeId, RequestedCover cover, DecisionItemRequest? terms, DateOnly start) => new()
    {
        Id = Guid.CreateVersion7(), PolicyId = policyId, InsuredLifeId = insuredLifeId, BenefitCode = cover.BenefitCode, SumAssured = cover.SumAssured,
        Premium = cover.Premium, ExclusionCodes = terms?.ExclusionCode is { } exclusion ? [exclusion] : null, StartDate = start,
        EndDate = start.AddYears(cover.TermYears).AddDays(-1),
    };

    internal void MarkClaimed() => Status = ClaimedStatus;
}

/// <summary>Entity LifeBeneficiary (life.beneficiary): a share from a date, until a date (both included).</summary>
public sealed class LifeBeneficiary : Entity<Guid>, ITenantScoped
{
    private LifeBeneficiary()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid PolicyId { get; private set; }

    public Guid PartyId { get; private set; }

    public string RelationshipCode { get; private set; } = "";

    public decimal SharePct { get; private set; }

    public DateOnly ValidFrom { get; private set; }

    public DateOnly? ValidTo { get; private set; }

    public long RowVersion { get; private set; }

    public bool ValidOn(DateOnly day) => ValidFrom <= day && (ValidTo is null || day <= ValidTo);

    internal static LifeBeneficiary Create(Guid policyId, BeneficiaryNomination nomination, DateOnly from) => new()
    {
        Id = Guid.CreateVersion7(), PolicyId = policyId, PartyId = nomination.PartyId, RelationshipCode = Guard.MaxLength(nomination.RelationshipCode, 15),
        SharePct = nomination.SharePct, ValidFrom = from,
    };

    internal void End(DateOnly lastDay) => ValidTo = lastDay;
}

/// <summary>Entity LifePremiumSchedule (life.premium_schedule): the premium per instalment, and arrears (LIF-005).</summary>
public sealed class LifePremiumSchedule : Entity<Guid>, ITenantScoped
{
    private LifePremiumSchedule()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid PolicyId { get; private set; }

    public string Frequency { get; private set; } = "";

    public decimal Premium { get; private set; }

    public DateOnly NextDueDate { get; private set; }

    /// <summary>The last day of the grace period, which starts at the oldest unpaid due date.</summary>
    public DateOnly? GraceUntil { get; private set; }

    public int MissedCount { get; private set; }

    public DateOnly? ArrearsSince { get; private set; }

    public long RowVersion { get; private set; }

    internal static LifePremiumSchedule Create(Guid policyId, string frequency, decimal premium, DateOnly firstDue) => new()
    {
        Id = Guid.CreateVersion7(), PolicyId = policyId, Frequency = frequency, Premium = premium, NextDueDate = firstDue,
    };

    internal void Arrears(int missed, DateOnly? oldestDue, int graceDays)
    {
        MissedCount = missed;
        ArrearsSince = missed == 0 ? null : oldestDue;
        GraceUntil = missed == 0 || oldestDue is null ? null : oldestDue.Value.AddDays(graceDays);
    }

    internal void Restart(DateOnly nextDue)
    {
        Arrears(0, null, 0);
        NextDueDate = nextDue;
    }
}

/// <summary>Entity PolicyEvent (life.policy_event): what changed, before and after. Insert-only.</summary>
public sealed class PolicyEvent : Entity<Guid>, ITenantScoped
{
    private PolicyEvent()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid PolicyId { get; private set; }

    public string EventType { get; private set; } = "";

    public DateOnly EffectiveDate { get; private set; }

    public Dictionary<string, string?> Before { get; private set; } = [];

    public Dictionary<string, string?> After { get; private set; } = [];

    public Guid? DocumentId { get; private set; }

    /// <summary>The user, or Guid.Empty for a scheduled job.</summary>
    public Guid RecordedBy { get; private set; }

    internal static PolicyEvent Create(Guid policyId, string eventType, DateOnly effectiveDate, Dictionary<string, string?> before, Dictionary<string, string?> after,
        Guid recordedBy) => new()
    {
        Id = Guid.CreateVersion7(), PolicyId = policyId, EventType = Guard.MaxLength(eventType, 20), EffectiveDate = effectiveDate, Before = before, After = after,
        RecordedBy = recordedBy,
    };
}

/// <summary>Entity (life.lapse_reinstatement): lapse, reinstatement and termination history. Insert-only.</summary>
public sealed class LapseReinstatement : Entity<Guid>, ITenantScoped
{
    public const string Lapse = "LAPSE";
    public const string Reinstate = "REINSTATE";
    public const string Terminate = "TERMINATE";

    private LapseReinstatement()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid PolicyId { get; private set; }

    public string EventType { get; private set; } = "";

    public DateOnly EffectiveDate { get; private set; }

    public string Reason { get; private set; } = "";

    public Guid? EvidenceRef { get; private set; }

    internal static LapseReinstatement Create(Guid policyId, string eventType, DateOnly effectiveDate, string reason, Guid? evidenceRef) => new()
    {
        Id = Guid.CreateVersion7(), PolicyId = policyId, EventType = eventType, EffectiveDate = effectiveDate, Reason = Guard.MaxLength(reason, 20), EvidenceRef = evidenceRef,
    };
}

/// <summary>
/// The LifePolicy aggregate (life.policy and its benefits, beneficiaries, premium schedule, events and lapse history).
/// It references the approved product and rating versions it was issued on (LIF-003); beneficiary shares total 100% on
/// every date; each alteration is checked against its state and the alteration type (LIF-004); lapse and reinstatement
/// follow the product's grace and reinstatement rules (LIF-005). TODO(spec): paid-up, surrender and maturity values
/// need actuarial calculations (OQ-052); their transitions are modelled but no command applies them.
/// </summary>
public sealed class LifePolicy : AggregateRoot<Guid>
{
    public static readonly StateMachine<LifePolicyStatus, LifePolicyTrigger> Lifecycle = new StateMachine<LifePolicyStatus, LifePolicyTrigger>()
        .Allow(LifePolicyStatus.Issued, LifePolicyTrigger.InceptionDateReached, LifePolicyStatus.InForce)
        .Allow(LifePolicyStatus.InForce, LifePolicyTrigger.LapsePolicy, LifePolicyStatus.Lapsed)
        .Allow(LifePolicyStatus.Lapsed, LifePolicyTrigger.ReinstatePolicy, LifePolicyStatus.InForce)
        .Allow(LifePolicyStatus.Lapsed, LifePolicyTrigger.ReinstatementWindowPassed, LifePolicyStatus.Terminated)
        .Allow(LifePolicyStatus.InForce, LifePolicyTrigger.MakePaidUp, LifePolicyStatus.PaidUp)
        .Allow(LifePolicyStatus.InForce, LifePolicyTrigger.SurrenderPolicy, LifePolicyStatus.Surrendered)
        .Allow(LifePolicyStatus.InForce, LifePolicyTrigger.CloseOnClaim, LifePolicyStatus.Claimed)
        .Allow(LifePolicyStatus.InForce, LifePolicyTrigger.MaturePolicy, LifePolicyStatus.Matured);

    private readonly List<PolicyInsuredLife> _lives = [];
    private readonly List<LifeBenefit> _benefits = [];
    private readonly List<LifeBeneficiary> _beneficiaries = [];
    private readonly List<LifePremiumSchedule> _schedules = [];
    private readonly List<PolicyEvent> _events = [];
    private readonly List<LapseReinstatement> _lapses = [];

    private LifePolicy()
    {
    }

    public string PolicyNumber { get; private set; } = "";

    public Guid ProposalId { get; private set; }

    public string ProductCode { get; private set; } = "";

    public int ProductVersion { get; private set; }

    public int RatingVersion { get; private set; }

    public int? RulesetVersion { get; private set; }

    public Guid PolicyholderPartyId { get; private set; }

    public LifePolicyStatus Status { get; private set; }

    public DateOnly InceptionDate { get; private set; }

    public DateOnly? TermEndDate { get; private set; }

    public string Currency { get; private set; } = "";

    public string PremiumFrequency { get; private set; } = "";

    public Guid BillingAccountRef { get; private set; }

    public Guid? PayerPartyId { get; private set; }

    public string? CollectionMethod { get; private set; }

    public string? IntermediaryCode { get; private set; }

    public IReadOnlyList<PolicyInsuredLife> Lives => _lives;

    public IReadOnlyList<LifeBenefit> Benefits => _benefits;

    public IReadOnlyList<LifeBeneficiary> Beneficiaries => _beneficiaries;

    public IReadOnlyList<PolicyEvent> Events => _events;

    public IReadOnlyList<LapseReinstatement> LapseHistory => _lapses;

    public IReadOnlyList<LifePremiumSchedule> Schedules => _schedules;

    public LifePremiumSchedule Schedule => _schedules.Single();

    public LifeBillingArrangement Billing => new(BillingAccountRef, PayerPartyId ?? PolicyholderPartyId, PremiumFrequency, CollectionMethod ?? "");

    public DateOnly? LapsedOn => Status is LifePolicyStatus.Lapsed or LifePolicyStatus.Terminated
        ? _lapses.Where(l => l.EventType == LapseReinstatement.Lapse).MaxBy(l => l.EffectiveDate)?.EffectiveDate
        : null;

    public IEnumerable<LifeBeneficiary> BeneficiariesOn(DateOnly day) => _beneficiaries.Where(b => b.ValidOn(day));

    /// <summary>
    /// Command IssuePolicy, from the accepted proposal with the decided terms. Exclusions are carried to their benefits.
    /// A loading or postponement changes the premium, which needs rating rules actuarial has not defined (OQ-052), so
    /// such terms are refused.
    /// </summary>
    public static Result<LifePolicy> IssuePolicy(Guid id, string policyNumber, LifeProposal proposal, LifeUnderwritingCase decided, Guid billingAccountRef, DateOnly today,
        DateTimeOffset now)
    {
        if (proposal.Status != ProposalStatus.Accepted || decided.Status != CaseStatus.Decided || decided.Outcome != UnderwritingOutcome.Accept
            || decided.ProposalId != proposal.Id)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"A policy is issued from a proposal accepted by underwriting; {proposal.ProposalNumber} is {proposal.Status}.");
        }

        var terms = decided.Items.Select(i => i.ToRequest()).ToList();
        if (terms.FirstOrDefault(t => t.IsLoaded || t.PostponedUntil is not null) is { } changed)
        {
            return Error.Conflict(LifeErrorCodes.LoadedTermsNotRated,
                $"{changed.BenefitCode} is accepted with a loading or postponement; its premium needs rating rules not yet defined (OQ-052).");
        }

        var inception = proposal.InceptionDate ?? today;
        var billing = proposal.Billing;
        var policy = new LifePolicy
        {
            Id = id, PolicyNumber = Guard.MaxLength(policyNumber, 15), ProposalId = proposal.Id, ProductCode = proposal.ProductCode, ProductVersion = proposal.ProductVersion,
            RatingVersion = proposal.RatingVersion!.Value, RulesetVersion = decided.RulesetVersion, PolicyholderPartyId = proposal.ApplicantPartyId,
            Status = LifePolicyStatus.Issued, InceptionDate = inception, Currency = proposal.Currency, PremiumFrequency = proposal.Frequency,
            BillingAccountRef = billingAccountRef, PayerPartyId = billing.PayerPartyId, CollectionMethod = billing.CollectionMethod, IntermediaryCode = proposal.IntermediaryCode,
        };
        foreach (var life in proposal.Lives)
        {
            var insured = PolicyInsuredLife.Create(id, life);
            policy._lives.Add(insured);
            policy._benefits.AddRange(proposal.Covers.Where(c => c.InsuredLifeId == life.Id)
                .Select(c => LifeBenefit.Create(id, insured.Id, c, terms.FirstOrDefault(t => t.BenefitCode == c.BenefitCode), inception)));
        }

        policy._beneficiaries.AddRange(proposal.BeneficiaryNominations.Select(n => LifeBeneficiary.Create(id, n, inception)));
        policy.TermEndDate = policy._benefits.Max(b => b.EndDate);
        policy._schedules.Add(LifePremiumSchedule.Create(id, proposal.Frequency, proposal.QuotedPremium!.Value, inception));
        policy.Raise(new LifePolicyIssued(id, policy.PolicyNumber, proposal.Id, policy.ProductCode, policy.ProductVersion, policy.RatingVersion, policy.PolicyholderPartyId,
            policy.Billing, policy.Currency, proposal.QuotedPremium!.Value, inception, policy.TermEndDate, policy._benefits.Sum(b => b.SumAssured), now));
        if (inception <= today)
        {
            policy.Fire(LifePolicyTrigger.InceptionDateReached);
        }

        return policy;
    }

    /// <summary>Transition "Inception date reached".</summary>
    public Result StartCover(DateOnly today) =>
        Status == LifePolicyStatus.Issued && InceptionDate <= today
            ? Fire(LifePolicyTrigger.InceptionDateReached)
            : Error.Conflict(ErrorCodes.InvalidStateTransition, $"Policy {PolicyNumber} is {Status}, from {InceptionDate:yyyy-MM-dd}.");

    /// <summary>Command ChangeBeneficiaries from a date: the new nominations total 100% (or there are none), within the product's rule.</summary>
    public Result ChangeBeneficiaries(IReadOnlyList<BeneficiaryNomination> nominations, DateOnly effectiveDate, NominationRule? rule, Guid by, DateTimeOffset now)
    {
        if (Status is not (LifePolicyStatus.Issued or LifePolicyStatus.InForce or LifePolicyStatus.Lapsed or LifePolicyStatus.PaidUp))
        {
            return Error.Conflict(LifeErrorCodes.AlterationNotAllowed, $"Beneficiaries are not changed on a policy that is {Status}.");
        }

        if (effectiveDate < InceptionDate || _beneficiaries.Any(b => b.ValidFrom > effectiveDate))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "A change takes effect on or after the inception date and the last change.");
        }

        var valid = Nominations.Validate(nominations, rule);
        if (valid.IsFailure)
        {
            return valid;
        }

        var current = BeneficiariesOn(effectiveDate).ToList();
        var before = current.ToDictionary(b => b.PartyId.ToString(), b => (string?)b.SharePct.ToString("0.00", CultureInfo.InvariantCulture));
        foreach (var beneficiary in current)
        {
            beneficiary.End(effectiveDate.AddDays(-1));
        }

        _beneficiaries.AddRange(nominations.Select(n => LifeBeneficiary.Create(Id, n, effectiveDate)));
        _events.Add(PolicyEvent.Create(Id, "BENEFICIARIES", effectiveDate, before,
            nominations.ToDictionary(n => n.PartyId.ToString(), n => (string?)n.SharePct.ToString("0.00", CultureInfo.InvariantCulture)), by));
        Raise(new LifePolicyChanged(Id, PolicyNumber, "BENEFICIARIES", effectiveDate, now));
        return Result.Success();
    }

    /// <summary>
    /// Command AlterPolicy (LIF-004): allowed only in the alteration type's states and without re-underwriting. The
    /// change applied is the servicing intermediary. TODO(spec): alterations that change cover or premium need
    /// re-rating (OQ-052) or re-underwriting, and the authority role is not checked against the user's roles (OQ-061).
    /// </summary>
    public Result AlterPolicy(AlterationType alteration, string? intermediaryCode, DateOnly effectiveDate, Guid by, DateTimeOffset now)
    {
        var state = UpperSnake(Status.ToString());
        if (!alteration.AllowedStates.Contains(state, StringComparer.OrdinalIgnoreCase))
        {
            return Error.Conflict(LifeErrorCodes.AlterationNotAllowed, $"{alteration.Code} is not allowed on a policy that is {state}.");
        }

        if (alteration.ReUnderwriting)
        {
            return Error.Conflict(LifeErrorCodes.AlterationNotAllowed, $"{alteration.Code} needs re-underwriting, which is not yet supported.");
        }

        var before = new Dictionary<string, string?> { ["intermediary_code"] = IntermediaryCode };
        IntermediaryCode = intermediaryCode;
        _events.Add(PolicyEvent.Create(Id, alteration.Code, effectiveDate, before, new Dictionary<string, string?> { ["intermediary_code"] = intermediaryCode }, by));
        Raise(new LifePolicyChanged(Id, PolicyNumber, alteration.Code, effectiveDate, now));
        return Result.Success();
    }

    /// <summary>
    /// Command RecordMissedPremium: the unpaid instalments past their due date, from Billing. The grace period starts at
    /// the oldest of them; when none remain (a payment in grace), it is cleared.
    /// </summary>
    public Result RecordArrears(int overdueInstalments, DateOnly? oldestDue, GraceRule grace)
    {
        if (Status is not (LifePolicyStatus.InForce or LifePolicyStatus.Issued))
        {
            return Result.Success();
        }

        var schedule = Schedule;
        if (schedule.MissedCount == overdueInstalments && schedule.ArrearsSince == oldestDue)
        {
            return Result.Success();
        }

        schedule.Arrears(overdueInstalments, oldestDue, grace.GraceDays);
        MarkChanged();
        return Result.Success();
    }

    /// <summary>Command LapsePolicy: the grace period has ended and the missed payments reach the product's limit (LIF-005). Cover ends the day after grace.</summary>
    public Result LapsePolicy(LapseRule rule, DateOnly today, DateTimeOffset now)
    {
        var schedule = Schedule;
        if (Status != LifePolicyStatus.InForce || schedule.GraceUntil is not { } graceUntil || graceUntil >= today || schedule.MissedCount < rule.MissedPaymentsToLapse)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition,
                $"Policy {PolicyNumber} lapses after its grace period with {rule.MissedPaymentsToLapse} missed payments; it has {schedule.MissedCount}, grace until {schedule.GraceUntil:yyyy-MM-dd}.");
        }

        var result = Fire(LifePolicyTrigger.LapsePolicy);
        if (result.IsSuccess)
        {
            var lapseDate = graceUntil.AddDays(1);
            _lapses.Add(LapseReinstatement.Create(Id, LapseReinstatement.Lapse, lapseDate, "MISSED_PREMIUM", null));
            Raise(new LifePolicyLapsed(Id, PolicyNumber, lapseDate, schedule.MissedCount, ProductCode, ProductVersion, Currency, now));
        }

        return result;
    }

    /// <summary>
    /// Command ReinstatePolicy within the product's reinstatement window, with evidence where the rule requires it.
    /// Premiums restart from the reinstatement date. TODO(spec): whether arrears must be paid first is not specified.
    /// </summary>
    public Result ReinstatePolicy(LapseRule rule, DateOnly today, Guid? evidenceRef, Guid by, DateTimeOffset now)
    {
        if (Status != LifePolicyStatus.Lapsed || LapsedOn is not { } lapsedOn)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Policy {PolicyNumber} is {Status}.");
        }

        if (IsoPeriod.Add(lapsedOn, rule.ReinstatementWindow) is not { } windowEnd || today > windowEnd)
        {
            return Error.Conflict(LifeErrorCodes.ReinstatementNotPermitted, $"The reinstatement window ({rule.ReinstatementWindow} from {lapsedOn:yyyy-MM-dd}) has passed.");
        }

        if (rule.EvidenceRequired != "NONE" && evidenceRef is null)
        {
            return Error.Validation(LifeErrorCodes.ReinstatementNotPermitted, $"Reinstatement needs evidence ({rule.EvidenceRequired}).");
        }

        var result = Fire(LifePolicyTrigger.ReinstatePolicy);
        if (result.IsSuccess)
        {
            Schedule.Restart(today);
            _lapses.Add(LapseReinstatement.Create(Id, LapseReinstatement.Reinstate, today, rule.EvidenceRequired, evidenceRef));
            _events.Add(PolicyEvent.Create(Id, "REINSTATEMENT", today, new() { ["status"] = "LAPSED" }, new() { ["status"] = "IN_FORCE" }, by));
            Raise(new LifePolicyReinstated(Id, PolicyNumber, today, TermEndDate, now));
        }

        return result;
    }

    /// <summary>Transition "Reinstatement window passed": a lapsed policy is terminated.</summary>
    public Result TerminateLapsed(LapseRule rule, DateOnly today, DateTimeOffset now)
    {
        if (Status != LifePolicyStatus.Lapsed || LapsedOn is not { } lapsedOn || IsoPeriod.Add(lapsedOn, rule.ReinstatementWindow) is not { } windowEnd || today <= windowEnd)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Policy {PolicyNumber} is {Status} and still within its reinstatement window.");
        }

        var result = Fire(LifePolicyTrigger.ReinstatementWindowPassed);
        if (result.IsSuccess)
        {
            _lapses.Add(LapseReinstatement.Create(Id, LapseReinstatement.Terminate, today, "WINDOW_PASSED", null));
            Raise(new LifePolicyChanged(Id, PolicyNumber, "TERMINATED", today, now));
        }

        return result;
    }

    /// <summary>
    /// Whether the policy covers an event on a date for a benefit on an insured life (LIF-008): in force (or lapsed
    /// after the event), with cover during any grace period the event falls in, and an active benefit then.
    /// </summary>
    public Result<LifeBenefit> CoversEvent(Guid insuredLifeId, string benefitCode, DateOnly eventDate, GraceRule? grace)
    {
        var inForce = Status == LifePolicyStatus.InForce || (Status is LifePolicyStatus.Lapsed or LifePolicyStatus.Terminated && LapsedOn is { } lapsed && eventDate < lapsed);
        if (!inForce || eventDate < InceptionDate)
        {
            return Error.Validation(LifeErrorCodes.NotCovered, $"Policy {PolicyNumber} was not in force on {eventDate:yyyy-MM-dd}.");
        }

        var schedule = Schedule;
        if (schedule.ArrearsSince is { } since && schedule.GraceUntil is { } until && eventDate >= since && eventDate <= until && grace is { CoverDuringGrace: false })
        {
            return Error.Validation(LifeErrorCodes.NotCovered, "The event was in a grace period without cover.");
        }

        return _benefits.FirstOrDefault(b => b.InsuredLifeId == insuredLifeId && b.BenefitCode == benefitCode && b.CoversOn(eventDate)) is { } benefit
            ? benefit
            : Error.Validation(LifeErrorCodes.NotCovered, $"No active {benefitCode} benefit on that life on {eventDate:yyyy-MM-dd}.");
    }

    /// <summary>Command CloseOnClaim: the paid benefits end; once none is active the policy is Claimed.</summary>
    public Result CloseOnClaim(IReadOnlyCollection<Guid> paidBenefitIds, DateOnly paidOn, DateTimeOffset now)
    {
        foreach (var benefit in _benefits.Where(b => paidBenefitIds.Contains(b.Id) && b.Status == LifeBenefit.Active))
        {
            benefit.MarkClaimed();
        }

        MarkChanged();
        if (Status != LifePolicyStatus.InForce || _benefits.Any(b => b.Status == LifeBenefit.Active))
        {
            return Result.Success();
        }

        var result = Fire(LifePolicyTrigger.CloseOnClaim);
        if (result.IsSuccess)
        {
            Raise(new LifePolicyChanged(Id, PolicyNumber, "CLAIMED", paidOn, now));
        }

        return result;
    }

    internal static string UpperSnake(string pascal) =>
        string.Concat(pascal.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + c : c.ToString())).ToUpperInvariant();

    private Result Fire(LifePolicyTrigger trigger)
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
