using Insys.BuildingBlocks.Domain;

namespace Insys.Life.Domain.Groups;

/// <summary>States from domain-model.json (life › GroupScheme).</summary>
public enum SchemeStatus
{
    SetUp,
    Active,
    Terminated,
}

public enum SchemeTrigger
{
    StartDateReached,
    RenewScheme,
    TerminateScheme,
}

public static class EvidenceStatus
{
    public const string NotRequired = "NOT_REQUIRED";
    public const string Required = "REQUIRED";
    public const string Accepted = "ACCEPTED";
}

/// <summary>A scheme's member category (life.cfg_member_category_definitions): cover is the benefit multiple of salary.</summary>
public sealed record MemberCategory(string CategoryCode, decimal BenefitMultiple);

public sealed record GroupMemberEnrolled(Guid SchemeId, string SchemeNumber, Guid MemberId, Guid PartyId, string CategoryCode, decimal CoverAmount, bool EvidenceRequired,
    DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record GroupSchemeRenewed(Guid SchemeId, string SchemeNumber, DateOnly RenewalDate, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>Entity GroupMember (life.group_member): one category, a cover amount, and whether cover above the free cover limit has evidence.</summary>
public sealed class GroupMember : Entity<Guid>, ITenantScoped
{
    private GroupMember()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid SchemeId { get; private set; }

    public Guid PartyId { get; private set; }

    public string CategoryCode { get; private set; } = "";

    public decimal? AnnualSalary { get; private set; }

    public decimal CoverAmount { get; private set; }

    public DateOnly JoinedOn { get; private set; }

    public DateOnly? LeftOn { get; private set; }

    public string EvidenceStatus { get; private set; } = Groups.EvidenceStatus.NotRequired;

    public decimal? FreeCoverLimit { get; private set; }

    public long RowVersion { get; private set; }

    public bool Current => LeftOn is null;

    internal static GroupMember Create(Guid schemeId, Guid partyId, DateOnly joinedOn) => new() { Id = Guid.CreateVersion7(), SchemeId = schemeId, PartyId = partyId, JoinedOn = joinedOn };

    /// <summary>Cover is salary × multiple; above the free cover limit it needs underwriting evidence (accepted evidence stays accepted unless cover rises).</summary>
    internal void Set(MemberCategory category, decimal? salary, decimal? freeCoverLimit)
    {
        var cover = Math.Round((salary ?? 0) * category.BenefitMultiple, 2, MidpointRounding.AwayFromZero);
        var rose = cover > CoverAmount;
        CategoryCode = Guard.MaxLength(category.CategoryCode, 10);
        AnnualSalary = salary;
        CoverAmount = cover;
        FreeCoverLimit = freeCoverLimit;
        if (freeCoverLimit is null || cover <= freeCoverLimit)
        {
            EvidenceStatus = Groups.EvidenceStatus.NotRequired;
        }
        else if (rose || EvidenceStatus != Groups.EvidenceStatus.Accepted)
        {
            EvidenceStatus = Groups.EvidenceStatus.Required;
        }
    }

    internal void AcceptEvidence() => EvidenceStatus = Groups.EvidenceStatus.Accepted;

    internal void Leave(DateOnly on) => LeftOn = on;
}

/// <summary>
/// The GroupScheme aggregate (life.group_scheme, group_member): an employer's scheme on a Life product. Each member
/// belongs to exactly one category; cover above the free cover limit needs underwriting evidence. TODO(spec): group
/// premium rating (OQ-052) and the scheme term on renewal (one year here; OQ-061) are not specified.
/// </summary>
public sealed class GroupScheme : AggregateRoot<Guid>
{
    public static readonly StateMachine<SchemeStatus, SchemeTrigger> Lifecycle = new StateMachine<SchemeStatus, SchemeTrigger>()
        .Allow(SchemeStatus.SetUp, SchemeTrigger.StartDateReached, SchemeStatus.Active)
        .Allow(SchemeStatus.Active, SchemeTrigger.RenewScheme, SchemeStatus.Active)
        .Allow(SchemeStatus.Active, SchemeTrigger.TerminateScheme, SchemeStatus.Terminated);

    private readonly List<GroupMember> _members = [];

    private GroupScheme()
    {
    }

    public string SchemeNumber { get; private set; } = "";

    public Guid EmployerPartyId { get; private set; }

    public string ProductCode { get; private set; } = "";

    public int ProductVersion { get; private set; }

    public string? SchemeType { get; private set; }

    public SchemeStatus Status { get; private set; }

    public DateOnly? StartDate { get; private set; }

    public DateOnly RenewalDate { get; private set; }

    public string? Currency { get; private set; }

    public IReadOnlyList<GroupMember> Members => _members;

    public int CurrentMembers => _members.Count(m => m.Current);

    public static Result<GroupScheme> SetUpScheme(Guid id, string schemeNumber, Guid employerPartyId, string productCode, int productVersion, string schemeType,
        DateOnly startDate, DateOnly renewalDate, string currency, DateOnly today)
    {
        if (renewalDate <= startDate)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "The renewal date is after the start date.");
        }

        var scheme = new GroupScheme
        {
            Id = id, SchemeNumber = Guard.MaxLength(schemeNumber, 12), EmployerPartyId = Guard.NotEmpty(employerPartyId), ProductCode = productCode,
            ProductVersion = productVersion, SchemeType = schemeType, Status = SchemeStatus.SetUp, StartDate = startDate, RenewalDate = renewalDate, Currency = currency,
        };
        if (startDate <= today)
        {
            scheme.Fire(SchemeTrigger.StartDateReached);
        }

        scheme.MarkChanged();
        return scheme;
    }

    /// <summary>Transition "Start date reached".</summary>
    public Result Activate(DateOnly today) =>
        Status == SchemeStatus.SetUp && StartDate <= today ? Fire(SchemeTrigger.StartDateReached)
            : Error.Conflict(ErrorCodes.InvalidStateTransition, $"Scheme {SchemeNumber} is {Status}, from {StartDate:yyyy-MM-dd}.");

    /// <summary>Command EnrolMember: one current membership per person, in one category, with its free cover limit.</summary>
    public Result<GroupMember> EnrolMember(Guid partyId, MemberCategory category, decimal? salary, DateOnly joinedOn, decimal? freeCoverLimit, DateTimeOffset now)
    {
        if (Status == SchemeStatus.Terminated)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Scheme {SchemeNumber} is terminated.");
        }

        if (_members.Any(m => m.Current && m.PartyId == partyId))
        {
            return Error.Conflict(ErrorCodes.DuplicateRecord, "The person is already a member of the scheme.");
        }

        if (category.BenefitMultiple > 0 && salary is not > 0)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "Cover is a multiple of salary: the salary is required.");
        }

        var member = GroupMember.Create(Id, partyId, joinedOn);
        member.Set(category, salary, freeCoverLimit);
        _members.Add(member);
        Raise(new GroupMemberEnrolled(Id, SchemeNumber, member.Id, partyId, member.CategoryCode, member.CoverAmount, member.EvidenceStatus == EvidenceStatus.Required, now));
        return member;
    }

    public Result ChangeMember(Guid memberId, MemberCategory category, decimal? salary, decimal? freeCoverLimit)
    {
        if (Member(memberId) is not { Current: true } member)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The member was not found.");
        }

        member.Set(category, salary, freeCoverLimit);
        MarkChanged();
        return Result.Success();
    }

    public Result AcceptEvidence(Guid memberId)
    {
        if (Member(memberId) is not { Current: true, EvidenceStatus: EvidenceStatus.Required } member)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "The member has no evidence outstanding.");
        }

        member.AcceptEvidence();
        MarkChanged();
        return Result.Success();
    }

    public Result RemoveMember(Guid memberId, DateOnly leftOn)
    {
        if (Member(memberId) is not { Current: true } member || leftOn < member.JoinedOn)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "A current member leaves on or after joining.");
        }

        member.Leave(leftOn);
        MarkChanged();
        return Result.Success();
    }

    public Result RenewScheme(DateTimeOffset now)
    {
        var result = Fire(SchemeTrigger.RenewScheme);
        if (result.IsSuccess)
        {
            RenewalDate = RenewalDate.AddYears(1);
            Raise(new GroupSchemeRenewed(Id, SchemeNumber, RenewalDate, now));
        }

        return result;
    }

    public Result TerminateScheme(DateOnly on)
    {
        var result = Fire(SchemeTrigger.TerminateScheme);
        if (result.IsSuccess)
        {
            foreach (var member in _members.Where(m => m.Current))
            {
                member.Leave(on < member.JoinedOn ? member.JoinedOn : on);
            }
        }

        return result;
    }

    private GroupMember? Member(Guid id) => _members.SingleOrDefault(m => m.Id == id);

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
