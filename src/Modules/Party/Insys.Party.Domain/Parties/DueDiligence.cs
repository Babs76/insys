/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Party
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

namespace Insys.Party.Domain.Parties;

/// <summary>The due diligence vocabulary (addenda/due-diligence.md §3), as party.due_diligence and its checks store it.</summary>
public static class DueDiligenceCodes
{
    public const string NotStarted = "NOT_STARTED";
    public const string InProgress = "IN_PROGRESS";
    public const string Cleared = "CLEARED";
    public const string Referred = "REFERRED";
    public const string Blocked = "BLOCKED";
    public const string Expired = "EXPIRED";

    public const string Simplified = "SIMPLIFIED";
    public const string Standard = "STANDARD";
    public const string Enhanced = "ENHANCED";

    public const string Identity = "IDENTITY";
    public const string Address = "ADDRESS";
    public const string Sanctions = "SANCTIONS";
    public const string Pep = "PEP";
    public const string AdverseMedia = "ADVERSE_MEDIA";
    public const string BeneficialOwnership = "BENEFICIAL_OWNERSHIP";
    public const string SourceOfFunds = "SOURCE_OF_FUNDS";

    public const string TrueMatch = "TRUE_MATCH";
    public const string FalsePositive = "FALSE_POSITIVE";

    /// <summary>What is outstanding while enhanced due diligence waits for its approval (OQ-126).</summary>
    public const string EnhancedApproval = "ENHANCED_APPROVAL";

    /// <summary>What is outstanding while a hit waits for review.</summary>
    public const string HitReview = "HIT_REVIEW";

    public static readonly IReadOnlyList<string> CheckTypes = [Identity, Address, Sanctions, Pep, AdverseMedia, BeneficialOwnership, SourceOfFunds];

    /// <summary>The checks a provider runs by screening; the others are verified by a person against a document, or by a provider (IDENTITY).</summary>
    public static readonly IReadOnlyList<string> ScreeningTypes = [Sanctions, Pep, AdverseMedia];

    public static readonly IReadOnlyList<string> Levels = [Simplified, Standard, Enhanced];

    public static readonly IReadOnlyList<string> RiskClasses = ["LOW", "MEDIUM", "HIGH"];
}

/// <summary>
/// A due diligence check run on a party (party.due_diligence_check, addenda/due-diligence.md §3): by a provider, with its
/// reference, or by a person against a document. It holds no number, only the identifier it is about. Never changed.
/// </summary>
public sealed class DueDiligenceCheck : AggregateRoot<Guid>
{
    public const string Identity = DueDiligenceCodes.Identity;
    public const string Passed = "PASSED";
    public const string Failed = "FAILED";
    public const string Hit = "HIT";

    private DueDiligenceCheck()
    {
    }

    public Guid PartyId { get; private set; }

    public string CheckType { get; private set; } = "";

    public string Outcome { get; private set; } = "";

    public Guid? IdentifierId { get; private set; }

    public string? PartnerCode { get; private set; }

    public string? ProviderRef { get; private set; }

    public Guid? DocumentId { get; private set; }

    public string? Reason { get; private set; }

    /// <summary>For a screening, the provider's list version it ran against (OQ-125).</summary>
    public string? ListVersion { get; private set; }

    public DateTimeOffset CheckedAt { get; private set; }

    public Guid? CheckedBy { get; private set; }

    /// <summary>F001: a provider's answer on an identifier.</summary>
    public static DueDiligenceCheck ByProvider(Guid partyId, Guid identifierId, string partnerCode, bool verified, string? providerRef, string? reason, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(), PartyId = partyId, CheckType = Identity, Outcome = verified ? Passed : Failed, IdentifierId = identifierId,
            PartnerCode = Guard.MaxLength(partnerCode, 15), ProviderRef = providerRef is null ? null : Guard.MaxLength(providerRef, 80),
            Reason = reason is null ? null : Guard.MaxLength(reason, 200), CheckedAt = now,
        };

    /// <summary>F001: a person verified an identifier against a document filed against the party.</summary>
    public static DueDiligenceCheck ByPerson(Guid partyId, Guid identifierId, Guid documentId, Guid checkedBy, string reference, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(), PartyId = partyId, CheckType = Identity, Outcome = Passed, IdentifierId = identifierId, DocumentId = documentId,
            ProviderRef = Guard.MaxLength(reference, 80), CheckedAt = now, CheckedBy = checkedBy,
        };

    /// <summary>F003: a screening of one list by a provider: HIT when it matched, PASSED when not.</summary>
    public static DueDiligenceCheck Screening(Guid partyId, string listType, string partnerCode, bool hit, string providerRef, string listVersion, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(), PartyId = partyId, CheckType = listType, Outcome = hit ? Hit : Passed, PartnerCode = Guard.MaxLength(partnerCode, 15),
            ProviderRef = Guard.MaxLength(providerRef, 80), ListVersion = Guard.MaxLength(listVersion, 40), CheckedAt = now,
        };

    /// <summary>
    /// F002, F003: a person verified a check against a document filed against the party: an address, beneficial ownership,
    /// or (enhanced due diligence) the source of funds or wealth. A provider's checks (screening, identity) are not recorded this way.
    /// </summary>
    public static Result<DueDiligenceCheck> Manual(Guid partyId, string checkType, Guid documentId, Guid checkedBy, string? reference, DateTimeOffset now)
    {
        if (!DueDiligenceCodes.CheckTypes.Contains(checkType) || DueDiligenceCodes.ScreeningTypes.Contains(checkType) || checkType == Identity)
        {
            return Error.Validation(ErrorCodes.ValidationFailed,
                $"{checkType} is not recorded by a person: screening runs through the provider, and an identifier is verified on the identifier.");
        }

        return new DueDiligenceCheck
        {
            Id = Guid.CreateVersion7(), PartyId = partyId, CheckType = checkType, Outcome = Passed, DocumentId = documentId, CheckedBy = checkedBy,
            ProviderRef = reference is null ? null : Guard.MaxLength(reference, 80), CheckedAt = now,
        };
    }
}

/// <summary>
/// A match a screening returned (party.screening_hit, §3): the list, the name matched, its score, and its review. A true
/// sanctions match needs a report to the FIC (s28A), whose reference staff record once made. HIGHLY_SENSITIVE: none of it
/// leaves Party in an event, a log line or an exception.
/// </summary>
public sealed class ScreeningHit : Entity<Guid>, ITenantScoped
{
    public const string S28A = "S28A";
    public const string S29 = "S29";

    private ScreeningHit()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid DueDiligenceId { get; private set; }

    public Guid CheckId { get; private set; }

    public string ListType { get; private set; } = "";

    public string ListName { get; private set; } = "";

    public string MatchedName { get; private set; } = "";

    public decimal Score { get; private set; }

    public string? ProviderRef { get; private set; }

    public DateTimeOffset RaisedAt { get; private set; }

    public string? Review { get; private set; }

    public string? ReviewReason { get; private set; }

    public Guid? ReviewedBy { get; private set; }

    public DateTimeOffset? ReviewedAt { get; private set; }

    public bool ReportRequired { get; private set; }

    public string? ReportType { get; private set; }

    public string? FicReference { get; private set; }

    public Guid? ReportedBy { get; private set; }

    public DateTimeOffset? ReportedAt { get; private set; }

    public long RowVersion { get; private set; }

    public bool Open => Review is null;

    internal static ScreeningHit Raise(Guid dueDiligenceId, Guid checkId, string listType, string listName, string matchedName, decimal score, string? providerRef,
        DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(), DueDiligenceId = dueDiligenceId, CheckId = checkId, ListType = listType, ListName = Guard.MaxLength(listName, 80),
        MatchedName = matchedName.Length <= 150 ? matchedName : matchedName[..150], Score = score, ProviderRef = providerRef is null ? null : Guard.MaxLength(providerRef, 80),
        RaisedAt = now,
    };

    internal void Decide(bool trueMatch, string reason, Guid reviewer, DateTimeOffset now)
    {
        Review = trueMatch ? DueDiligenceCodes.TrueMatch : DueDiligenceCodes.FalsePositive;
        ReviewReason = Guard.MaxLength(Guard.NotNullOrWhiteSpace(reason), 200);
        ReviewedBy = reviewer;
        ReviewedAt = now;
        ReportRequired = trueMatch && ListType == DueDiligenceCodes.Sanctions;
    }

    internal void Reported(string reportType, string reference, Guid by, DateTimeOffset now)
    {
        ReportType = reportType;
        FicReference = Guard.MaxLength(Guard.NotNullOrWhiteSpace(reference), 40);
        ReportedBy = by;
        ReportedAt = now;
    }
}

/// <summary>The risk a party's facts give (F077): its class, the score, the due diligence level and the review interval of the class.</summary>
public sealed record RiskAssessment(string RiskClass, int Score, string Level, string ReviewInterval);

/// <summary>
/// Aggregate DueDiligence (party.due_diligence, addenda/due-diligence.md §3, §4): one per party. Its status follows from
/// what the requirement sets still need (worked out by the application from the checks), the hits waiting for review, a
/// true sanctions match (blocked until a second person unblocks it), enhanced due diligence's approval, and the review
/// date. Changes raise DueDiligenceStatusChanged, with the party and status only, and Cleared, Referred, Blocked or Expired.
/// </summary>
public sealed class DueDiligence : AggregateRoot<Guid>
{
    private readonly List<ScreeningHit> _hits = [];
    private bool _screened;

    private DueDiligence()
    {
    }

    public Guid PartyId { get; private set; }

    public string Status { get; private set; } = DueDiligenceCodes.NotStarted;

    public string? RiskClass { get; private set; }

    public int? RiskScore { get; private set; }

    public bool RiskClassOverridden { get; private set; }

    public string Level { get; private set; } = DueDiligenceCodes.Standard;

    public DateOnly? AssessedOn { get; private set; }

    /// <summary>The checks the requirement sets still need, and HIT_REVIEW or ENHANCED_APPROVAL while those wait.</summary>
    public string[] Outstanding { get; private set; } = [];

    public DateTimeOffset? ClearedAt { get; private set; }

    public Guid? ClearedBy { get; private set; }

    public DateOnly? NextReviewOn { get; private set; }

    public DateTimeOffset? LastScreenedAt { get; private set; }

    public string? ScreenedListVersion { get; private set; }

    public bool Blocked { get; private set; }

    public Guid? EnhancedRequestedBy { get; private set; }

    public DateTimeOffset? EnhancedApprovedAt { get; private set; }

    public Guid? EnhancedApprovalId { get; private set; }

    public string? OverrideClass { get; private set; }

    public string? OverrideReason { get; private set; }

    public Guid? OverrideRequestedBy { get; private set; }

    public string? UnblockReason { get; private set; }

    public Guid? UnblockRequestedBy { get; private set; }

    public IReadOnlyList<ScreeningHit> Hits => _hits;

    public bool HasOpenHit => _hits.Any(h => h.Open);

    /// <summary>A true PEP match (§4.3): a fact for the risk class, and the level is at least ENHANCED.</summary>
    public bool IsPep => _hits.Any(h => h.ListType == DueDiligenceCodes.Pep && h.Review == DueDiligenceCodes.TrueMatch);

    public static DueDiligence Start(Guid partyId) => new() { Id = Guid.CreateVersion7(), PartyId = partyId };

    /// <summary>
    /// F077: the class the factors give, unless a person's override stands. A true PEP match raises the level to ENHANCED
    /// whatever the class (§4.3).
    /// </summary>
    public void Classify(RiskAssessment assessment, Func<string, RiskAssessment?> forClass)
    {
        var chosen = RiskClassOverridden && RiskClass is { } overridden && forClass(overridden) is { } kept ? kept with { Score = assessment.Score } : assessment;
        RiskClass = chosen.RiskClass;
        RiskScore = chosen.Score;
        Level = IsPep ? DueDiligenceCodes.Enhanced : chosen.Level;
        MarkChanged();
    }

    /// <summary>F003: a screening's answer: each match a hit for review; a party screened with none clears what it needed.</summary>
    public void RecordScreening(Guid checkId, string listType, string listVersion, IEnumerable<(string ListName, string MatchedName, decimal Score, string ProviderRef)> matches,
        DateTimeOffset now)
    {
        foreach (var match in matches)
        {
            _hits.Add(ScreeningHit.Raise(Id, checkId, listType, match.ListName, match.MatchedName, match.Score, match.ProviderRef, now));
        }

        LastScreenedAt = now;
        ScreenedListVersion = Guard.MaxLength(listVersion, 40);
        _screened = true;
        MarkChanged();
    }

    /// <summary>
    /// Command ReviewHit (§4.3): a person decides a hit, with a reason. A true sanctions match blocks the party and needs a
    /// report to the FIC; a true PEP match raises the level to ENHANCED. A hit already decided is not decided again.
    /// </summary>
    public Result ReviewHit(Guid hitId, bool trueMatch, string reason, Guid reviewer, DateTimeOffset now)
    {
        if (_hits.SingleOrDefault(h => h.Id == hitId) is not { } hit)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The hit was not found.");
        }

        if (!hit.Open)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "The hit has already been reviewed.");
        }

        hit.Decide(trueMatch, reason, reviewer, now);
        if (trueMatch && hit.ListType == DueDiligenceCodes.Sanctions)
        {
            Blocked = true;
        }

        if (trueMatch && hit.ListType == DueDiligenceCodes.Pep)
        {
            Level = DueDiligenceCodes.Enhanced;
        }

        MarkChanged();
        return Result.Success();
    }

    /// <summary>Command RecordFicReport (§3): the FIC reference of the report staff made for a true sanctions match (s28A) or a suspicion (s29).</summary>
    public Result RecordReport(Guid hitId, string reportType, string reference, Guid by, DateTimeOffset now)
    {
        if (_hits.SingleOrDefault(h => h.Id == hitId) is not { } hit)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The hit was not found.");
        }

        if (hit.Review != DueDiligenceCodes.TrueMatch)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "A report is recorded for a hit reviewed as a true match.");
        }

        if (hit.FicReference is not null)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "The hit's report is already recorded.");
        }

        hit.Reported(reportType, reference, by, now);
        MarkChanged();
        return Result.Success();
    }

    /// <summary>
    /// Works out the status from what is still needed (the application reads the requirement sets and checks). Blocked
    /// stays BLOCKED; an open hit REFERRED; anything outstanding IN_PROGRESS (NOT_STARTED before any check). With nothing
    /// outstanding at ENHANCED, clearance waits for the approval, which is asked for once, with the actor as maker
    /// (OQ-126); otherwise the party is CLEARED, next reviewed on the date its class's interval gives. An EXPIRED party stays so
    /// until a fresh screening has run.
    /// </summary>
    public void Assess(IReadOnlyList<string> missing, bool anyCheck, bool freshSinceExpiry, DateOnly today, DateOnly? nextReviewIfCleared, Guid? actor, DateTimeOffset now)
    {
        var outstanding = missing.ToList();
        string status;
        if (Blocked)
        {
            status = DueDiligenceCodes.Blocked;
        }
        else if (HasOpenHit)
        {
            status = DueDiligenceCodes.Referred;
            outstanding.Add(DueDiligenceCodes.HitReview);
        }
        else if (Status == DueDiligenceCodes.Expired && !freshSinceExpiry)
        {
            status = DueDiligenceCodes.Expired;
        }
        else if (outstanding.Count > 0)
        {
            status = anyCheck ? DueDiligenceCodes.InProgress : DueDiligenceCodes.NotStarted;
        }
        else if (Level == DueDiligenceCodes.Enhanced && EnhancedApprovedAt is null)
        {
            status = DueDiligenceCodes.InProgress;
            outstanding.Add(DueDiligenceCodes.EnhancedApproval);
            // BR-109: with no person acting (a provider's result came last, or a job), the platform requests it. A request
            // recorded with no requester before BR-109 never reached Workflow, so it is asked again.
            if (EnhancedRequestedBy is null || EnhancedRequestedBy == Guid.Empty)
            {
                EnhancedRequestedBy = actor ?? MakerChecker.SystemMaker;
                Raise(new EnhancedDueDiligenceApprovalRequested(Id, PartyId, EnhancedRequestedBy.Value, now));
            }
        }
        else
        {
            status = DueDiligenceCodes.Cleared;
        }

        AssessedOn = today;
        Outstanding = [.. outstanding];
        var changed = status != Status;
        if (changed && status == DueDiligenceCodes.Cleared)
        {
            ClearedAt = now;
            ClearedBy = EnhancedApprovedAt is null ? null : ClearedBy;
            NextReviewOn = nextReviewIfCleared;
        }

        Status = status;
        if (changed || _screened)
        {
            Raise(new DueDiligenceStatusChanged(PartyId, Status, changed, now));
        }

        if (changed && status is DueDiligenceCodes.Cleared or DueDiligenceCodes.Referred or DueDiligenceCodes.Blocked)
        {
            Raise(new DueDiligenceReached(PartyId, status, now));
        }

        _screened = false;
        MarkChanged();
    }

    /// <summary>
    /// Ongoing due diligence (§4.3): a cleared party past its review date is EXPIRED, and is no longer cleared. Enhanced due
    /// diligence is approved again on review.
    /// </summary>
    public bool Expire(DateOnly today, DateTimeOffset now)
    {
        if (Status != DueDiligenceCodes.Cleared || NextReviewOn is not { } due || due > today)
        {
            return false;
        }

        Status = DueDiligenceCodes.Expired;
        EnhancedApprovedAt = null;
        EnhancedApprovalId = null;
        EnhancedRequestedBy = null;
        Raise(new DueDiligenceStatusChanged(PartyId, Status, true, now));
        Raise(new DueDiligenceReached(PartyId, Status, now));
        MarkChanged();
        return true;
    }

    /// <summary>True when the granted enhanced approval was already applied (a redelivered grant).</summary>
    public bool EnhancedApplied(Guid approvalId) => EnhancedApprovalId == approvalId;

    /// <summary>
    /// Workflow's decision on enhanced due diligence (OQ-126): approved, the approver is recorded as clearing it; any other
    /// ending clears the request, and the next assessment asks again. The maker cannot approve their own (SRS-002).
    /// </summary>
    public Result DecideEnhanced(bool approved, Guid approvalId, Guid checkerId, DateTimeOffset now)
    {
        if (EnhancedRequestedBy is not { } maker || EnhancedApprovedAt is not null)
        {
            return approved ? Error.Conflict(ErrorCodes.InvalidStateTransition, "No enhanced due diligence approval is waiting.") : Result.Success();
        }

        if (approved && (maker == checkerId || MakerChecker.IsSystem(checkerId)))
        {
            return MakerChecker.EnsureDifferentPeople(maker.ToString(), checkerId.ToString());
        }

        if (approved)
        {
            EnhancedApprovedAt = now;
            EnhancedApprovalId = approvalId;
            ClearedBy = checkerId;
        }
        else
        {
            EnhancedRequestedBy = null;
        }

        MarkChanged();
        return Result.Success();
    }

    /// <summary>Command OverrideRiskClass (§4.4): a person sets the class, with a reason; it applies once a second person approves it.</summary>
    public Result RequestOverride(string riskClass, string reason, Guid requestedBy, DateTimeOffset now)
    {
        if (OverrideRequestedBy is not null)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "An override of the risk class is already waiting for approval.");
        }

        OverrideClass = riskClass;
        OverrideReason = Guard.MaxLength(Guard.NotNullOrWhiteSpace(reason), 200);
        OverrideRequestedBy = requestedBy;
        Raise(new RiskClassOverrideRequested(Id, PartyId, riskClass, requestedBy, OverrideReason, now));
        MarkChanged();
        return Result.Success();
    }

    /// <summary>Workflow's decision on an override: approved, the class is the one asked for, and kept on reclassification.</summary>
    public Result DecideOverride(bool approved, Guid checkerId)
    {
        if (OverrideRequestedBy is not { } maker || OverrideClass is not { } requested)
        {
            return approved ? Error.Conflict(ErrorCodes.InvalidStateTransition, "No override of the risk class is waiting.") : Result.Success();
        }

        if (approved && maker == checkerId)
        {
            return MakerChecker.EnsureDifferentPeople(maker.ToString(), checkerId.ToString());
        }

        if (approved)
        {
            RiskClass = requested;
            RiskClassOverridden = true;
        }

        OverrideClass = null;
        OverrideRequestedBy = null;
        MarkChanged();
        return Result.Success();
    }

    /// <summary>Command UnblockDueDiligence (§4.3): privileged, with a reason; it applies once a second person approves it.</summary>
    public Result RequestUnblock(string reason, Guid requestedBy, DateTimeOffset now)
    {
        if (!Blocked)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "The party's due diligence is not blocked.");
        }

        if (UnblockRequestedBy is not null)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "Unblocking is already waiting for approval.");
        }

        UnblockReason = Guard.MaxLength(Guard.NotNullOrWhiteSpace(reason), 200);
        UnblockRequestedBy = requestedBy;
        Raise(new DueDiligenceUnblockRequested(Id, PartyId, requestedBy, UnblockReason, now));
        MarkChanged();
        return Result.Success();
    }

    /// <summary>Workflow's decision on unblocking: approved by a second person, the block is lifted; otherwise it stays.</summary>
    public Result DecideUnblock(bool approved, Guid checkerId)
    {
        if (UnblockRequestedBy is not { } maker)
        {
            return approved ? Error.Conflict(ErrorCodes.InvalidStateTransition, "No unblocking is waiting.") : Result.Success();
        }

        if (approved && maker == checkerId)
        {
            return MakerChecker.EnsureDifferentPeople(maker.ToString(), checkerId.ToString());
        }

        if (approved)
        {
            Blocked = false;
        }

        UnblockReason = null;
        UnblockRequestedBy = null;
        MarkChanged();
        return Result.Success();
    }
}

/// <summary>
/// An organisation's beneficial owner (party.beneficial_owner, FICA s21B): the owning party, which has due diligence of
/// its own, its share or control, and how it was established (a document filed against the organisation, the companies
/// register, or a declaration).
/// </summary>
public sealed class BeneficialOwner : AggregateRoot<Guid>
{
    public static readonly IReadOnlyList<string> ControlTypes = ["OWNERSHIP", "CONTROL", "SENIOR_MANAGER"];

    public static readonly IReadOnlyList<string> EstablishedBy = ["DOCUMENT", "REGISTER", "DECLARATION"];

    private BeneficialOwner()
    {
    }

    public Guid PartyId { get; private set; }

    public Guid OwnerPartyId { get; private set; }

    public string ControlType { get; private set; } = "";

    public decimal? SharePercent { get; private set; }

    public string Established { get; private set; } = "";

    public Guid? DocumentId { get; private set; }

    public Guid RecordedBy { get; private set; }

    public DateTimeOffset RecordedAt { get; private set; }

    public static Result<BeneficialOwner> Record(Guid partyId, Guid ownerPartyId, string controlType, decimal? sharePercent, string established, Guid? documentId,
        Guid recordedBy, DateTimeOffset now)
    {
        if (ownerPartyId == partyId)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "An organisation is not its own beneficial owner.");
        }

        if (established == "DOCUMENT" && documentId is null)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "Ownership established by a document names the document.");
        }

        return new BeneficialOwner
        {
            Id = Guid.CreateVersion7(), PartyId = partyId, OwnerPartyId = ownerPartyId, ControlType = controlType, SharePercent = sharePercent, Established = established,
            DocumentId = documentId, RecordedBy = recordedBy, RecordedAt = now,
        };
    }
}

/// <summary>The status changed, or a screening ran: holders of gated steps ask again (§5). Party and status only.</summary>
public sealed record DueDiligenceStatusChanged(Guid PartyId, string Status, bool StatusChanged, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>The status reached CLEARED, REFERRED, BLOCKED or EXPIRED: published as that status's own event (§9).</summary>
public sealed record DueDiligenceReached(Guid PartyId, string Status, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>Enhanced due diligence asks Workflow for approval by a holder of PARTY_APPROVE_ENHANCED_DD (OQ-126).</summary>
public sealed record EnhancedDueDiligenceApprovalRequested(Guid DueDiligenceId, Guid PartyId, Guid RequestedBy, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>An override of the risk class asks Workflow for a second person's approval (§4.4).</summary>
public sealed record RiskClassOverrideRequested(Guid DueDiligenceId, Guid PartyId, string RiskClass, Guid RequestedBy, string Reason, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>Unblocking asks Workflow for a second person's approval (§4.3).</summary>
public sealed record DueDiligenceUnblockRequested(Guid DueDiligenceId, Guid PartyId, Guid RequestedBy, string Reason, DateTimeOffset OccurredAt) : IDomainEvent;
