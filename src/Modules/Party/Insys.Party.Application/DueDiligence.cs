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
using System.Globalization;
using FluentValidation;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
using Insys.Integration.Contracts;
using Insys.Party.Contracts;
using Insys.Party.Domain.Parties;

namespace Insys.Party.Application;

/// <summary>A party's due diligence and its beneficial owners (party.due_diligence, party.beneficial_owner).</summary>
public interface IDueDiligenceStore
{
    Task<DueDiligence?> ForParty(Guid partyId, CancellationToken cancellationToken);

    Task<IReadOnlyList<BeneficialOwner>> Owners(Guid partyId, CancellationToken cancellationToken);

    /// <summary>GET /party/due-diligence: newest first by ID, filtered.</summary>
    Task<IReadOnlyList<DueDiligence>> List(DueDiligenceFilter filter, Guid? beforeId, int limit, CancellationToken cancellationToken);

    /// <summary>The daily job's work: cleared records past their review date, and parties to screen again (an older list version, or a month since).</summary>
    Task<(IReadOnlyList<Guid> Expiring, IReadOnlyList<Guid> Rescreen)> Due(DateOnly today, string? listVersion, DateTimeOffset screenedBefore, CancellationToken cancellationToken);

    void Add(DueDiligence dueDiligence);

    void Add(BeneficialOwner owner);
}

public sealed record DueDiligenceFilter(string? Status, string? RiskClass, DateOnly? ReviewDueBy, bool? OpenHits);

/// <summary>A weighted risk factor (party.cfg_customer_risk_factors).</summary>
public sealed record RiskFactor(string Factor, IReadOnlyList<string> MatchValues, int Weight);

/// <summary>A risk class's score band, level and review interval (party.cfg_customer_risk_classes).</summary>
/// <param name="ReviewMode">OQ-138: PERSON (the default) or AUTOMATIC, how an expired party's periodic review ends.</param>
public sealed record RiskClassBand(string RiskClass, int MinScore, int? MaxScore, string Level, string ReviewInterval, string ReviewMode = DueDiligenceCodes.ReviewByPerson);

/// <summary>The due diligence configuration in force: a tenant's own rows replace the platform's for the same key (scope GT).</summary>
public interface IDueDiligenceConfiguration
{
    /// <summary>The check types the requirement sets need for the party type at the level, in any of the domains.</summary>
    Task<IReadOnlyList<string>> Requirements(string partyType, string level, IReadOnlyCollection<string> domains, DateOnly day, CancellationToken cancellationToken);

    Task<IReadOnlyList<RiskFactor>> RiskFactors(DateOnly day, CancellationToken cancellationToken);

    Task<IReadOnlyList<RiskClassBand>> RiskClasses(DateOnly day, CancellationToken cancellationToken);

    /// <summary>Whether adverse media is screened for the tenant (off unless switched on, POPIA §1).</summary>
    Task<bool> AdverseMediaEnabled(DateOnly day, CancellationToken cancellationToken);

    /// <summary>The day an ISO 8601 interval (a class's review interval) after the day; three years when it does not parse.</summary>
    DateOnly After(DateOnly day, string interval);

    /// <summary>The day an ISO 8601 interval (a gate's screening age) before the day; 30 days when it does not parse.</summary>
    DateOnly Before(DateOnly day, string interval);

    /// <summary>The tenant's setting, or null with no settings row in force (then the gates apply to a tenant entitled to LIFE, OQ-123).</summary>
    Task<bool?> GatesEnabled(DateOnly day, CancellationToken cancellationToken);

    /// <summary>The gate for a domain and step: the tenant's own, else the platform's; null when none.</summary>
    Task<(string Requirement, string? ScreeningMaxAge)?> Gate(string domain, string step, DateOnly day, CancellationToken cancellationToken);
}

/// <summary>§5: whether a party is cleared for a step in a domain (IDueDiligenceStatus asks it).</summary>
public sealed record DueDiligenceClearedFor(Guid PartyId, string Step, string Domain) : IQuery<DueDiligenceAnswer>;

/// <summary>
/// The gate check (addenda/due-diligence.md §5, OQ-123). With the tenant's gates off, or a gate of NONE, the party is
/// cleared. SCREENED needs a sanctions screening within the gate's age, no hit waiting for review and no block (s26B).
/// CLEARED needs the due diligence CLEARED as well. What is outstanding names check types and states, never a hit.
/// </summary>
internal sealed class DueDiligenceClearedForHandler(IDueDiligenceStore store, IDueDiligenceConfiguration configuration, Tenancy.Contracts.ITenantFunctionSettings tenantSettings,
    ITenantContext tenant, IBusinessDate businessDate) : IQueryHandler<DueDiligenceClearedFor, DueDiligenceAnswer>
{
    public async Task<Result<DueDiligenceAnswer>> Handle(DueDiligenceClearedFor query, CancellationToken cancellationToken)
    {
        var today = businessDate.Today;
        var enabled = await configuration.GatesEnabled(today, cancellationToken)
                      ?? (await tenantSettings.For(tenant.RequireTenantId(), today, cancellationToken)).EntitledFeatures.Contains("LIFE");
        if (!enabled || await configuration.Gate(query.Domain, query.Step, today, cancellationToken) is not { } gate || gate.Requirement == "NONE")
        {
            return DueDiligenceAnswer.Yes;
        }

        if (await store.ForParty(query.PartyId, cancellationToken) is not { } dueDiligence)
        {
            return new DueDiligenceAnswer(false, gate.Requirement == DueDiligenceCodes.Cleared ? [DueDiligenceCodes.NotStarted] : ["SCREENING"]);
        }

        var outstanding = new List<string>();
        if (dueDiligence.Blocked)
        {
            outstanding.Add(DueDiligenceCodes.Blocked);
        }

        if (dueDiligence.HasOpenHit)
        {
            outstanding.Add(DueDiligenceCodes.HitReview);
        }

        if (dueDiligence.LastScreenedAt is not { } screened
            || (gate.ScreeningMaxAge is { } age && businessDate.DateOf(screened) < configuration.Before(today, age))) // BR-112: counted in business days
        {
            outstanding.Add("SCREENING");
        }

        if (gate.Requirement == DueDiligenceCodes.Cleared && dueDiligence.Status != DueDiligenceCodes.Cleared)
        {
            outstanding.AddRange(dueDiligence.Outstanding.Length > 0 ? dueDiligence.Outstanding : [dueDiligence.Status]);
        }

        var distinct = outstanding.Distinct().ToList();
        return new DueDiligenceAnswer(distinct.Count == 0, distinct);
    }
}

/// <summary>IDueDiligenceStatus for other modules: the gate check, as a Party query in the caller's tenant.</summary>
public sealed class DueDiligenceStatus(IDispatcher dispatcher, ITenantContext tenant) : IDueDiligenceStatus
{
    public async Task<DueDiligenceAnswer> ClearedFor(Guid tenantId, Guid partyId, string step, string domain, CancellationToken cancellationToken)
    {
        if (tenant.RequireTenantId() != tenantId)
        {
            throw new InvalidOperationException("Due diligence is read in the caller's own tenant.");
        }

        var answer = await dispatcher.Query(new DueDiligenceClearedFor(partyId, step, domain), cancellationToken);
        return answer.IsSuccess ? answer.Value : throw new InvalidOperationException($"The due diligence gate could not be read: {answer.Error.Code}.");
    }
}

/// <summary>
/// The due diligence work every path shares (addenda/due-diligence.md §4): starting a party's record, screening it with
/// the tenant's provider, and assessing it again: the class from the risk factors (F077), what the requirement sets still
/// need (F002), and so the status. Personal data stays in Party: the audit names counts and codes, never a name or a hit.
/// </summary>
public sealed class DueDiligenceService(IPartyRepository parties, IDueDiligenceStore store, IDueDiligenceConfiguration configuration,
    Tenancy.Contracts.ITenantFunctionSettings tenantSettings, IPartnerDirectory partners, IScreeningAdapter screening, ITenantContext tenant, IBusinessDate businessDate,
    IClock clock, IAuditTrail audit)
{
    private static readonly string[] DomainFeatures = ["LIFE", "GENERAL", "HEALTH"];

    private readonly HashSet<Guid> _started = [];

    public async Task<DueDiligence> For(Guid partyId, CancellationToken cancellationToken)
    {
        if (await store.ForParty(partyId, cancellationToken) is { } existing)
        {
            return existing;
        }

        var started = DueDiligence.Start(partyId);
        store.Add(started);
        _started.Add(started.Id);
        return started;
    }

    /// <summary>The row version the record has once this request is saved: a changed existing record moves on by one; a new one is written at 1.</summary>
    public long SavedVersion(DueDiligence dueDiligence) => dueDiligence.RowVersion + (dueDiligence.HasChanges && !_started.Contains(dueDiligence.Id) ? 1 : 0);

    /// <summary>
    /// F003: screens the party on SANCTIONS and PEP, and ADVERSE_MEDIA where switched on, with the tenant's SCREENING
    /// partner: one check per list, each match a hit. Returns the checks, or none when no provider is active.
    /// </summary>
    public async Task<IReadOnlyList<DueDiligenceCheck>> Screen(Domain.Parties.Party party, DueDiligence dueDiligence, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        if (await partners.ActivePartner(tenantId, PartnerTypes.Screening, businessDate.Today, cancellationToken) is not { } partner)
        {
            return [];
        }

        List<string> lists = [ScreeningLists.Sanctions, ScreeningLists.Pep];
        if (await configuration.AdverseMediaEnabled(businessDate.Today, cancellationToken))
        {
            lists.Add(ScreeningLists.AdverseMedia);
        }

        var subject = party.Kind == PartyKind.Organisation
            ? new ScreeningSubject(true, party.Organisation?.LegalName ?? party.DisplayName, null, party.Addresses.FirstOrDefault(a => a.ValidTo is null)?.Country)
            : new ScreeningSubject(false, party.DisplayName, party.Individual?.BirthDate, party.Individual?.Nationality ?? party.Addresses.FirstOrDefault(a => a.ValidTo is null)?.Country);
        var result = await screening.Screen(tenantId, partner, subject, lists, cancellationToken);
        var now = clock.UtcNow;
        var checks = new List<DueDiligenceCheck>();
        foreach (var list in lists)
        {
            var matches = result.Matches.Where(m => m.ListType == list).ToList();
            var check = DueDiligenceCheck.Screening(party.Id, list, partner, matches.Count > 0, result.ProviderRef, result.ListVersion, now);
            parties.Add(check);
            checks.Add(check);
            dueDiligence.RecordScreening(check.Id, list, result.ListVersion, matches.Select(m => (m.ListName, m.MatchedName, m.Score, m.ProviderRef)), now);
        }

        audit.Record(new AuditEntry("PARTY", "ScreenParty", nameof(DueDiligence), dueDiligence.Id.ToString(), new Dictionary<string, string?>
        {
            ["party"] = party.Id.ToString(), ["partner"] = partner, ["lists"] = string.Join(",", lists),
            ["hits"] = result.Matches.Count.ToString(CultureInfo.InvariantCulture),
        }));
        return checks;
    }

    /// <summary>Assesses the party again (§4.2, §4.4): its class from the factors, what is missing, and its status.</summary>
    public async Task Reassess(Domain.Parties.Party party, DueDiligence dueDiligence, Guid? actor, IReadOnlyCollection<DueDiligenceCheck> pending,
        CancellationToken cancellationToken)
    {
        var today = businessDate.Today;
        var checks = (await parties.Checks(party.Id, cancellationToken)).Concat(pending).DistinctBy(c => c.Id).ToList();
        var owners = party.Kind == PartyKind.Organisation ? await store.Owners(party.Id, cancellationToken) : [];

        // F077: the weights of the factors the party's facts match add up to its score; the band gives class, level and review interval.
        var facts = Facts(party, dueDiligence, checks, today);
        var score = (await configuration.RiskFactors(today, cancellationToken))
            .Where(f => facts.TryGetValue(f.Factor, out var values) && values.Any(v => f.MatchValues.Contains(v, StringComparer.OrdinalIgnoreCase)))
            .Sum(f => f.Weight);
        var bands = await configuration.RiskClasses(today, cancellationToken);
        var band = bands.Where(b => score >= b.MinScore && (b.MaxScore is null || score <= b.MaxScore)).OrderByDescending(b => b.MinScore).FirstOrDefault();

        // TODO(spec): with no risk class in force (a tenant withdrew the platform's), the party is assessed as MEDIUM at STANDARD.
        RiskAssessment Of(RiskClassBand b) => new(b.RiskClass, score, b.Level, b.ReviewInterval);
        dueDiligence.Classify(band is null ? new RiskAssessment("MEDIUM", score, DueDiligenceCodes.Standard, "P3Y") : Of(band),
            c => bands.FirstOrDefault(b => b.RiskClass == c) is { } kept ? Of(kept) : null);

        // F002: the requirement sets of every domain the tenant works in; a check met before an expiry counts for a review only when fresh.
        var domains = (await tenantSettings.For(tenant.RequireTenantId(), today, cancellationToken)).EntitledFeatures.Intersect(DomainFeatures).ToList();
        var required = await configuration.Requirements(party.Kind == PartyKind.Organisation ? "ORGANISATION" : "INDIVIDUAL", dueDiligence.Level,
            domains.Count > 0 ? domains : DomainFeatures, today, cancellationToken);
        var adverseMedia = await configuration.AdverseMediaEnabled(today, cancellationToken);
        // BR-112: fresh for a review means on or after the review date as the tenant sees it, not after UTC midnight.
        var since = dueDiligence.ReviewDue && dueDiligence.NextReviewOn is { } due ? businessDate.StartOf(due) : (DateTimeOffset?)null;
        var missing = required.Distinct().Where(t => t != DueDiligenceCodes.AdverseMedia || adverseMedia).Where(t => !Met(t, checks, owners, since)).ToList();
        var fresh = since is { } from && checks.Any(c => c.CheckType == DueDiligenceCodes.Sanctions && c.CheckedAt >= from);
        var nextReview = configuration.After(today, band?.ReviewInterval ?? "P3Y");
        var reviewMode = bands.FirstOrDefault(b => b.RiskClass == dueDiligence.RiskClass)?.ReviewMode ?? DueDiligenceCodes.ReviewByPerson;
        dueDiligence.Assess(missing, checks.Count > 0, fresh, today, nextReview, actor, clock.UtcNow, reviewMode);
    }

    /// <summary>Screens (when a provider is active) and assesses again: what onboarding, a payout's re-screen and the jobs do.</summary>
    public async Task ScreenAndAssess(Domain.Parties.Party party, Guid? actor, CancellationToken cancellationToken)
    {
        var dueDiligence = await For(party.Id, cancellationToken);
        var screened = await Screen(party, dueDiligence, cancellationToken);
        await Reassess(party, dueDiligence, actor, screened, cancellationToken);
    }

    private static bool Met(string type, IReadOnlyList<DueDiligenceCheck> checks, IReadOnlyList<BeneficialOwner> owners, DateTimeOffset? since) => type switch
    {
        _ when DueDiligenceCodes.ScreeningTypes.Contains(type) => checks.Any(c => c.CheckType == type && c.Outcome != DueDiligenceCheck.Failed && (since is null || c.CheckedAt >= since)),
        DueDiligenceCodes.BeneficialOwnership => owners.Count > 0 && checks.Any(c => c.CheckType == type && c.Outcome == DueDiligenceCheck.Passed),
        _ => checks.Any(c => c.CheckType == type && c.Outcome == DueDiligenceCheck.Passed),
    };

    /// <summary>The party's facts the risk factors match on (§4.4). Occupation is not recorded on a party yet, so it matches nothing.</summary>
    private static Dictionary<string, IReadOnlyList<string>> Facts(Domain.Parties.Party party, DueDiligence dueDiligence, IReadOnlyList<DueDiligenceCheck> checks, DateOnly today)
    {
        var countries = party.Addresses.Where(a => a.ValidTo is null || a.ValidTo > today).Select(a => a.Country).ToList();
        if (party.Individual?.Nationality is { } nationality)
        {
            countries.Add(nationality.Trim());
        }

        return new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["COUNTRY"] = countries,
            ["PARTY_TYPE"] = [party.Kind == PartyKind.Organisation ? "ORGANISATION" : "INDIVIDUAL"],
            ["LEGAL_FORM"] = party.Organisation is { } o ? [o.LegalFormCode] : [],
            ["INDUSTRY"] = party.Organisation?.IndustryCode is { } industry ? [industry] : [],
            ["PEP"] = [dueDiligence.IsPep ? "TRUE" : "FALSE"],
            ["DOMAIN"] = [.. party.Roles.Where(r => r.ValidFrom <= today && (r.ValidTo is null || r.ValidTo > today)).Select(r => r.Domain).Distinct()],
            ["CHANNEL"] = [party.RegisteredBy is null ? "STAFF" : "PORTAL"],
            ["SOURCE_OF_FUNDS"] = [checks.Any(c => c.CheckType == DueDiligenceCodes.SourceOfFunds && c.Outcome == DueDiligenceCheck.Passed) ? "VERIFIED" : "UNVERIFIED"],
        };
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// Commands and queries (§9)
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>A command, though it changes nothing: each read is audited (IAM-006, §3), and only a command's audit entries are saved.</summary>
public sealed record GetDueDiligence(Guid PartyId) : ICommand<DueDiligenceView>;

/// <param name="CheckType">SCREENING (sanctions, PEP and, where on, adverse media, by the provider), IDENTITY with an identifier (by the provider), or ADDRESS, BENEFICIAL_OWNERSHIP or SOURCE_OF_FUNDS recorded by the caller against a document.</param>
public sealed record RunDueDiligenceCheck(Guid PartyId, string CheckType, Guid? IdentifierId, Guid? DocumentId, string? Reference) : ICommand<DueDiligenceView>;

public sealed record ReviewScreeningHit(Guid PartyId, Guid HitId, string Outcome, string Reason, long ExpectedVersion) : ICommand<DueDiligenceView>, IVersionedCommand;

/// <summary>
/// OQ-138: a person completes the periodic review of a party past its review date, confirming its details are current. A
/// fresh screening runs as part of it; identity and address checks are not redone (a changed detail is updated the usual way).
/// </summary>
/// <param name="DetailsCurrent">The reviewer confirms the party's details are current; it must be true.</param>
public sealed record CompleteDueDiligenceReview(Guid PartyId, bool DetailsCurrent, string? Note, long ExpectedVersion) : ICommand<DueDiligenceView>, IVersionedCommand;

public sealed record OverrideRiskClass(Guid PartyId, string RiskClass, string Reason) : ICommand<DueDiligenceView>;

public sealed record RecordFicReport(Guid PartyId, Guid HitId, string ReportType, string FicReference) : ICommand<DueDiligenceView>;

public sealed record RequestDueDiligenceUnblock(Guid PartyId, string Reason) : ICommand<DueDiligenceView>;

public sealed record RecordBeneficialOwner(Guid PartyId, Guid OwnerPartyId, string ControlType, decimal? SharePercent, string EstablishedBy, Guid? DocumentId)
    : ICommand<DueDiligenceView>;

public sealed record ListDueDiligence(DueDiligenceFilter Filter, Guid? BeforeId, int Limit) : IQuery<IReadOnlyList<DueDiligenceSummary>>;

/// <summary>The daily job (PARTY_DUE_DILIGENCE_JOBS): expires parties past review and screens again those due (OQ-125).</summary>
public sealed record RunDueDiligenceJobs : ICommand<DueDiligenceJobRun>;

public sealed record DueDiligenceJobRun(int Expired, int Rescreened);

/// <summary>A hit and its review. Only for holders of due_diligence.review_hit; HIGHLY_SENSITIVE.</summary>
public sealed record ScreeningHitView(Guid Id, [property: OneOf("SANCTIONS", "PEP", "ADVERSE_MEDIA")] string ListType, string ListName, string MatchedName, decimal Score,
    string? ProviderRef, DateTimeOffset RaisedAt, [property: OneOf("TRUE_MATCH", "FALSE_POSITIVE")] string? Review, string? ReviewReason, Guid? ReviewedBy,
    DateTimeOffset? ReviewedAt, bool ReportRequired, [property: OneOf("S28A", "S29")] string? ReportType, string? FicReference)
{
    public static ScreeningHitView From(ScreeningHit h) => new(h.Id, h.ListType, h.ListName, h.MatchedName, h.Score, h.ProviderRef, h.RaisedAt, h.Review, h.ReviewReason,
        h.ReviewedBy, h.ReviewedAt, h.ReportRequired, h.ReportType, h.FicReference);
}

public sealed record BeneficialOwnerView(Guid Id, Guid OwnerPartyId, [property: OneOf("OWNERSHIP", "CONTROL", "SENIOR_MANAGER")] string ControlType, decimal? SharePercent,
    [property: OneOf("DOCUMENT", "REGISTER", "DECLARATION")] string EstablishedBy, Guid? DocumentId, Guid RecordedBy, DateTimeOffset RecordedAt)
{
    public static BeneficialOwnerView From(BeneficialOwner o) => new(o.Id, o.OwnerPartyId, o.ControlType, o.SharePercent, o.Established, o.DocumentId, o.RecordedBy, o.RecordedAt);
}

/// <summary>
/// A party's due diligence (§9). Outstanding names the checks still needed, and HIT_REVIEW or ENHANCED_APPROVAL while they
/// wait. Hits is null unless the caller holds due_diligence.review_hit. Never shown on a portal (§6).
/// </summary>
public sealed record DueDiligenceView(Guid Id, Guid PartyId, [property: OneOf("NOT_STARTED", "IN_PROGRESS", "CLEARED", "REFERRED", "BLOCKED", "EXPIRED")] string Status,
    [property: OneOf("LOW", "MEDIUM", "HIGH")] string? RiskClass, int? RiskScore, bool RiskClassOverridden, [property: OneOf("SIMPLIFIED", "STANDARD", "ENHANCED")] string Level,
    IReadOnlyList<string> Outstanding, DateTimeOffset? ClearedAt, Guid? ClearedBy, DateOnly? NextReviewOn, DateTimeOffset? LastScreenedAt, bool Blocked,
    [property: OneOf("LOW", "MEDIUM", "HIGH")] string? PendingOverrideClass, bool UnblockPending, IReadOnlyList<DueDiligenceCheckView> Checks,
    IReadOnlyList<BeneficialOwnerView> BeneficialOwners, IReadOnlyList<ScreeningHitView>? Hits, long RowVersion,
    [property: BuildingBlocks.Contracts.AlwaysPresent] bool ReviewDue = false, [property: BuildingBlocks.Contracts.AlwaysPresent] DateTimeOffset? ReviewedAt = null,
    [property: BuildingBlocks.Contracts.AlwaysPresent] Guid? ReviewedBy = null)
{
    /// <summary>OQ-138: the review ended with no person (an AUTOMATIC class): ReviewedBy is the system requester.</summary>
    public bool ReviewedBySystem => ReviewedBy is { } by && MakerChecker.IsSystem(by);

    public static DueDiligenceView From(DueDiligence d, IEnumerable<DueDiligenceCheck> checks, IEnumerable<BeneficialOwner> owners, bool showHits, long rowVersion) =>
        new(d.Id, d.PartyId, d.Status, d.RiskClass, d.RiskScore, d.RiskClassOverridden, d.Level, d.Outstanding, d.ClearedAt, d.ClearedBy, d.NextReviewOn, d.LastScreenedAt,
            d.Blocked, d.OverrideClass, d.UnblockRequestedBy is not null, [.. checks.Select(DueDiligenceCheckView.From)], [.. owners.Select(BeneficialOwnerView.From)],
            showHits ? [.. d.Hits.OrderBy(h => h.RaisedAt).Select(ScreeningHitView.From)] : null, rowVersion, d.ReviewDue, d.ReviewedAt, d.ReviewedBy);
}

/// <summary>A row of the compliance work list (§9): no hit detail, only how many wait.</summary>
public sealed record DueDiligenceSummary(Guid Id, Guid PartyId, [property: OneOf("NOT_STARTED", "IN_PROGRESS", "CLEARED", "REFERRED", "BLOCKED", "EXPIRED")] string Status,
    [property: OneOf("LOW", "MEDIUM", "HIGH")] string? RiskClass, [property: OneOf("SIMPLIFIED", "STANDARD", "ENHANCED")] string Level, DateOnly? NextReviewOn,
    DateTimeOffset? LastScreenedAt, int OpenHits)
{
    public static DueDiligenceSummary From(DueDiligence d) => new(d.Id, d.PartyId, d.Status, d.RiskClass, d.Level, d.NextReviewOn, d.LastScreenedAt, d.Hits.Count(h => h.Open));
}

public sealed class RunDueDiligenceCheckValidator : AbstractValidator<RunDueDiligenceCheck>
{
    public RunDueDiligenceCheckValidator()
    {
        RuleFor(c => c.CheckType).Must(t => t is "SCREENING" or "IDENTITY" or "ADDRESS" or "BENEFICIAL_OWNERSHIP" or "SOURCE_OF_FUNDS")
            .WithMessage("checkType is SCREENING, IDENTITY, ADDRESS, BENEFICIAL_OWNERSHIP or SOURCE_OF_FUNDS.");
        RuleFor(c => c.IdentifierId).NotNull().When(c => c.CheckType == "IDENTITY").WithMessage("An identity check names the identifier.");
        RuleFor(c => c.DocumentId).NotNull().When(c => c.CheckType is "ADDRESS" or "BENEFICIAL_OWNERSHIP" or "SOURCE_OF_FUNDS")
            .WithMessage("A check recorded by a person names the document it was verified against.");
        RuleFor(c => c.Reference).MaximumLength(80);
    }
}

public sealed class CompleteDueDiligenceReviewValidator : AbstractValidator<CompleteDueDiligenceReview>
{
    public CompleteDueDiligenceReviewValidator()
    {
        RuleFor(c => c.DetailsCurrent).Equal(true).WithMessage("detailsCurrent must be true: the reviewer confirms the party's details are current.");
        RuleFor(c => c.Note).MaximumLength(500);
    }
}

public sealed class ReviewScreeningHitValidator : AbstractValidator<ReviewScreeningHit>
{
    public ReviewScreeningHitValidator()
    {
        RuleFor(c => c.Outcome).Must(o => o is DueDiligenceCodes.TrueMatch or DueDiligenceCodes.FalsePositive).WithMessage("outcome is TRUE_MATCH or FALSE_POSITIVE.");
        RuleFor(c => c.Reason).NotEmpty().MaximumLength(200);
    }
}

public sealed class OverrideRiskClassValidator : AbstractValidator<OverrideRiskClass>
{
    public OverrideRiskClassValidator()
    {
        RuleFor(c => c.RiskClass).Must(DueDiligenceCodes.RiskClasses.Contains).WithMessage("riskClass is LOW, MEDIUM or HIGH.");
        RuleFor(c => c.Reason).NotEmpty().MaximumLength(200);
    }
}

public sealed class RecordFicReportValidator : AbstractValidator<RecordFicReport>
{
    public RecordFicReportValidator()
    {
        RuleFor(c => c.ReportType).Must(t => t is ScreeningHit.S28A or ScreeningHit.S29).WithMessage("reportType is S28A or S29.");
        RuleFor(c => c.FicReference).NotEmpty().MaximumLength(40);
    }
}

public sealed class RequestDueDiligenceUnblockValidator : AbstractValidator<RequestDueDiligenceUnblock>
{
    public RequestDueDiligenceUnblockValidator() => RuleFor(c => c.Reason).NotEmpty().MaximumLength(200);
}

public sealed class RecordBeneficialOwnerValidator : AbstractValidator<RecordBeneficialOwner>
{
    public RecordBeneficialOwnerValidator()
    {
        RuleFor(c => c.ControlType).Must(BeneficialOwner.ControlTypes.Contains).WithMessage("controlType is OWNERSHIP, CONTROL or SENIOR_MANAGER.");
        RuleFor(c => c.EstablishedBy).Must(BeneficialOwner.EstablishedBy.Contains).WithMessage("establishedBy is DOCUMENT, REGISTER or DECLARATION.");
        RuleFor(c => c.SharePercent).InclusiveBetween(0.01m, 100m).PrecisionScale(5, 2, true).When(c => c.SharePercent is not null);
        RuleFor(c => c.DocumentId).NotNull().When(c => c.EstablishedBy == "DOCUMENT").WithMessage("Ownership established by a document names the document.");
    }
}

internal sealed class DueDiligenceHandlers(IPartyRepository parties, IDueDiligenceStore store, DueDiligenceService service, Documents.Contracts.IDocumentOwnership documents,
    IPartnerDirectory partners, IIdentityVerificationAdapter identity, BuildingBlocks.Application.Security.IFieldProtector protector, ITenantContext tenant,
    IExecutionContext execution, IBusinessDate businessDate, IClock clock, IAuditTrail audit) :
    ICommandHandler<GetDueDiligence, DueDiligenceView>,
    ICommandHandler<RunDueDiligenceCheck, DueDiligenceView>,
    ICommandHandler<ReviewScreeningHit, DueDiligenceView>,
    ICommandHandler<CompleteDueDiligenceReview, DueDiligenceView>,
    ICommandHandler<OverrideRiskClass, DueDiligenceView>,
    ICommandHandler<RecordFicReport, DueDiligenceView>,
    ICommandHandler<RequestDueDiligenceUnblock, DueDiligenceView>,
    ICommandHandler<RecordBeneficialOwner, DueDiligenceView>,
    IQueryHandler<ListDueDiligence, IReadOnlyList<DueDiligenceSummary>>
{
    public async Task<Result<DueDiligenceView>> Handle(GetDueDiligence query, CancellationToken cancellationToken)
    {
        if (await parties.Get(query.PartyId, cancellationToken) is null)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The party was not found.");
        }

        var dueDiligence = await store.ForParty(query.PartyId, cancellationToken);
        audit.Record(new AuditEntry("PARTY", "ReadDueDiligence", nameof(DueDiligence), (dueDiligence?.Id ?? query.PartyId).ToString(),
            new Dictionary<string, string?> { ["party"] = query.PartyId.ToString() }));
        return await View(query.PartyId, dueDiligence ?? DueDiligence.Start(query.PartyId), cancellationToken);
    }

    public async Task<Result<DueDiligenceView>> Handle(RunDueDiligenceCheck command, CancellationToken cancellationToken)
    {
        var user = Parties.User(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (await parties.Get(command.PartyId, cancellationToken) is not { } party)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The party was not found.");
        }

        var dueDiligence = await service.For(party.Id, cancellationToken);
        List<DueDiligenceCheck> added = [];
        switch (command.CheckType)
        {
            case "SCREENING":
                added.AddRange(await service.Screen(party, dueDiligence, cancellationToken));
                if (added.Count == 0)
                {
                    return Error.Validation(ErrorCodes.ValidationFailed, "The tenant has no active screening provider; screening is not available.");
                }

                break;
            case DueDiligenceCodes.Identity:
                var tenantId = tenant.RequireTenantId();
                if (party.Identifiers.SingleOrDefault(i => i.Id == command.IdentifierId) is not { } identifier)
                {
                    return Error.Validation(ErrorCodes.ValidationFailed, "identifierId is not an identifier of this party.");
                }

                if (await partners.ActivePartner(tenantId, PartnerTypes.IdentityVerification, businessDate.Today, cancellationToken) is not { } partner)
                {
                    return Error.Validation(ErrorCodes.ValidationFailed, "The tenant has no active identity verification provider; verify the identifier against a document.");
                }

                var result = await identity.Verify(tenantId, partner, identifier.DocType, identifier.Country, protector.Unprotect(identifier.NumberEnc),
                    party.Individual?.FirstNames, party.Individual?.Surname, party.Individual?.BirthDate, cancellationToken);
                var check = DueDiligenceCheck.ByProvider(party.Id, identifier.Id, partner, result.Verified, result.ProviderRef, result.Reason, clock.UtcNow);
                parties.Add(check);
                added.Add(check);
                if (result.Verified && result.ProviderRef is { } reference && identifier.VerifiedAt is null)
                {
                    party.VerifyIdentifier(identifier.Id, reference, clock.UtcNow);
                }

                break;
            default:
                if (!await documents.FiledAgainst(command.DocumentId!.Value, Documents.Contracts.PartyDocuments.RefType, party.Id, cancellationToken))
                {
                    return Error.Validation(ErrorCodes.ValidationFailed, "documentId is not a document filed against this party.");
                }

                var manual = DueDiligenceCheck.Manual(party.Id, command.CheckType, command.DocumentId.Value, user.Value, command.Reference, clock.UtcNow);
                if (manual.IsFailure)
                {
                    return manual.Error;
                }

                parties.Add(manual.Value);
                added.Add(manual.Value);
                break;
        }

        await service.Reassess(party, dueDiligence, user.Value, added, cancellationToken);
        audit.Record(new AuditEntry("PARTY", "RunDueDiligenceCheck", nameof(DueDiligence), dueDiligence.Id.ToString(), new Dictionary<string, string?>
        {
            ["party"] = party.Id.ToString(), ["checkType"] = command.CheckType, ["status"] = dueDiligence.Status,
        }));
        return await View(party.Id, dueDiligence, cancellationToken, added);
    }

    public async Task<Result<DueDiligenceView>> Handle(ReviewScreeningHit command, CancellationToken cancellationToken)
    {
        var user = Parties.User(execution);
        var loaded = await Load(command.PartyId, command.ExpectedVersion, cancellationToken);
        if (user.IsFailure || loaded.IsFailure)
        {
            return user.IsFailure ? user.Error : loaded.Error!;
        }

        var (party, dueDiligence) = loaded.Value;
        var reviewed = dueDiligence.ReviewHit(command.HitId, command.Outcome == DueDiligenceCodes.TrueMatch, command.Reason, user.Value, clock.UtcNow);
        if (reviewed.IsFailure)
        {
            return reviewed.Error;
        }

        await service.Reassess(party, dueDiligence, user.Value, [], cancellationToken);
        audit.Record(new AuditEntry("PARTY", "ReviewScreeningHit", nameof(DueDiligence), dueDiligence.Id.ToString(), new Dictionary<string, string?>
        {
            ["party"] = party.Id.ToString(), ["hit"] = command.HitId.ToString(), ["outcome"] = command.Outcome, ["status"] = dueDiligence.Status,
        }));
        return await View(party.Id, dueDiligence, cancellationToken);
    }

    public async Task<Result<DueDiligenceView>> Handle(CompleteDueDiligenceReview command, CancellationToken cancellationToken)
    {
        var user = Parties.User(execution);
        var loaded = await Load(command.PartyId, command.ExpectedVersion, cancellationToken);
        if (user.IsFailure || loaded.IsFailure)
        {
            return user.IsFailure ? user.Error : loaded.Error!;
        }

        // OQ-138: the identity check's document, when it records an expiry date that has passed, is replaced before the review completes.
        var (party, dueDiligence) = loaded.Value;
        var identityCheck = (await parties.Checks(party.Id, cancellationToken))
            .Where(c => c.CheckType == DueDiligenceCodes.Identity && c.Outcome == DueDiligenceCheck.Passed && c.IdentifierId is not null).MaxBy(c => c.CheckedAt);
        if (identityCheck is not null && party.ExpiredIdentityDocument(identityCheck.IdentifierId!.Value, businessDate.Today) is { } expiredType)
        {
            return Error.Validation(PartyErrorCodes.IdentityDocumentExpired,
                $"The {expiredType} used for the party's identity check has expired; record a newer one, then complete the review.");
        }

        var reviewed = dueDiligence.CompleteReview(user.Value, clock.UtcNow);
        if (reviewed.IsFailure)
        {
            return reviewed.Error;
        }

        await service.ScreenAndAssess(party, user.Value, cancellationToken);
        audit.Record(new AuditEntry("PARTY", "CompleteDueDiligenceReview", nameof(DueDiligence), dueDiligence.Id.ToString(), new Dictionary<string, string?>
        {
            ["party"] = party.Id.ToString(), ["status"] = dueDiligence.Status, ["note"] = command.Note,
        }));
        return await View(party.Id, dueDiligence, cancellationToken);
    }

    public async Task<Result<DueDiligenceView>> Handle(OverrideRiskClass command, CancellationToken cancellationToken) =>
        await Change(command.PartyId, "OverrideRiskClass", (d, user) => d.RequestOverride(command.RiskClass, command.Reason, user, clock.UtcNow),
            new Dictionary<string, string?> { ["riskClass"] = command.RiskClass }, cancellationToken);

    public async Task<Result<DueDiligenceView>> Handle(RecordFicReport command, CancellationToken cancellationToken) =>
        await Change(command.PartyId, "RecordFicReport", (d, user) => d.RecordReport(command.HitId, command.ReportType, command.FicReference, user, clock.UtcNow),
            new Dictionary<string, string?> { ["hit"] = command.HitId.ToString(), ["reportType"] = command.ReportType }, cancellationToken);

    public async Task<Result<DueDiligenceView>> Handle(RequestDueDiligenceUnblock command, CancellationToken cancellationToken) =>
        await Change(command.PartyId, "RequestUnblock", (d, user) => d.RequestUnblock(command.Reason, user, clock.UtcNow), [], cancellationToken);

    public async Task<Result<DueDiligenceView>> Handle(RecordBeneficialOwner command, CancellationToken cancellationToken)
    {
        var user = Parties.User(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (await parties.Get(command.PartyId, cancellationToken) is not { } party)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The party was not found.");
        }

        if (party.Kind != PartyKind.Organisation)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "Beneficial owners are recorded for an organisation.");
        }

        if (await parties.Get(command.OwnerPartyId, cancellationToken) is null)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "ownerPartyId is not a party.");
        }

        if ((await store.Owners(party.Id, cancellationToken)).Any(o => o.OwnerPartyId == command.OwnerPartyId))
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "The owner is already recorded for this organisation.");
        }

        if (command.DocumentId is { } documentId && !await documents.FiledAgainst(documentId, Documents.Contracts.PartyDocuments.RefType, party.Id, cancellationToken))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "documentId is not a document filed against this organisation.");
        }

        var owner = BeneficialOwner.Record(party.Id, command.OwnerPartyId, command.ControlType, command.SharePercent, command.EstablishedBy, command.DocumentId, user.Value,
            clock.UtcNow);
        if (owner.IsFailure)
        {
            return owner.Error;
        }

        store.Add(owner.Value);
        var dueDiligence = await service.For(party.Id, cancellationToken);
        audit.Record(new AuditEntry("PARTY", "RecordBeneficialOwner", nameof(DueDiligence), dueDiligence.Id.ToString(), new Dictionary<string, string?>
        {
            ["party"] = party.Id.ToString(), ["owner"] = command.OwnerPartyId.ToString(),
        }));
        return await View(party.Id, dueDiligence, cancellationToken, owners: [owner.Value]);
    }

    public async Task<Result<IReadOnlyList<DueDiligenceSummary>>> Handle(ListDueDiligence query, CancellationToken cancellationToken) =>
        (await store.List(query.Filter, query.BeforeId, query.Limit, cancellationToken)).Select(DueDiligenceSummary.From).ToList();

    private async Task<Result<(Domain.Parties.Party Party, DueDiligence DueDiligence)>> Load(Guid partyId, long? expected, CancellationToken cancellationToken)
    {
        if (await parties.Get(partyId, cancellationToken) is not { } party || await store.ForParty(partyId, cancellationToken) is not { } dueDiligence)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The party's due diligence was not found.");
        }

        return expected is { } v && dueDiligence.RowVersion != v
            ? Error.PreconditionFailed(ErrorCodes.PreconditionFailed, "The due diligence has changed since it was read.")
            : (party, dueDiligence);
    }

    private async Task<Result<DueDiligenceView>> Change(Guid partyId, string action, Func<DueDiligence, Guid, Result> change, Dictionary<string, string?> details,
        CancellationToken cancellationToken)
    {
        var user = Parties.User(execution);
        var loaded = await Load(partyId, null, cancellationToken);
        if (user.IsFailure || loaded.IsFailure)
        {
            return user.IsFailure ? user.Error : loaded.Error!;
        }

        var dueDiligence = loaded.Value.DueDiligence;
        var changed = change(dueDiligence, user.Value);
        if (changed.IsFailure)
        {
            return changed.Error;
        }

        details["party"] = partyId.ToString();
        audit.Record(new AuditEntry("PARTY", action, nameof(DueDiligence), dueDiligence.Id.ToString(), details));
        return await View(partyId, dueDiligence, cancellationToken);
    }

    private async Task<DueDiligenceView> View(Guid partyId, DueDiligence dueDiligence, CancellationToken cancellationToken, IReadOnlyCollection<DueDiligenceCheck>? added = null,
        IReadOnlyCollection<BeneficialOwner>? owners = null) =>
        DueDiligenceView.From(dueDiligence, (await parties.Checks(partyId, cancellationToken)).Concat(added ?? []).DistinctBy(c => c.Id).OrderBy(c => c.CheckedAt),
            (await store.Owners(partyId, cancellationToken)).Concat(owners ?? []).DistinctBy(o => o.Id),
            execution.HasPermission(PartyPermissions.DueDiligenceReviewHit), service.SavedVersion(dueDiligence));
}

/// <summary>
/// Onboarding (§4.3, §6): a party registered by staff or through a portal gets its due diligence record, is screened when
/// the tenant has a screening provider, and is assessed. Details that are risk factors (address, nationality, organisation)
/// assess it again.
/// </summary>
public sealed class DueDiligenceOnboarding(IPartyRepository parties, DueDiligenceService service) :
    IIntegrationEventHandler<Contracts.PartyRegistered>,
    IIntegrationEventHandler<Contracts.PartyDetailsChanged>,
    IIntegrationEventHandler<BuildingBlocks.Contracts.Payouts.PayeeScreeningRequested>
{
    public async Task Handle(Contracts.PartyRegistered registered, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (await parties.Get(registered.PartyId, cancellationToken) is { } party)
        {
            await service.ScreenAndAssess(party, null, cancellationToken);
        }
    }

    public async Task Handle(Contracts.PartyDetailsChanged changed, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (await parties.Get(changed.PartyId, cancellationToken) is { } party)
        {
            await service.Reassess(party, await service.For(party.Id, cancellationToken), null, [], cancellationToken);
        }
    }

    /// <summary>OQ-125: before each payout, a payee held by its gate is screened again (once a day at most), then assessed.</summary>
    public async Task Handle(BuildingBlocks.Contracts.Payouts.PayeeScreeningRequested requested, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (await parties.Get(requested.PartyId, cancellationToken) is not { } party)
        {
            return;
        }

        var dueDiligence = await service.For(party.Id, cancellationToken);
        if (dueDiligence.LastScreenedAt is { } last && last > envelope.OccurredAt.AddDays(-1))
        {
            await service.Reassess(party, dueDiligence, null, [], cancellationToken);
            return;
        }

        await service.ScreenAndAssess(party, null, cancellationToken);
    }
}

/// <summary>
/// Workflow's decisions on due diligence (OQ-126, §4.3, §4.4): enhanced due diligence, an override of the risk class, and
/// unblocking. Each then assesses the party again. A redelivered grant already applied changes nothing; one the record
/// will not take (nothing waits, or the checker is the maker) parks the delivery (D-5).
/// </summary>
public sealed class DueDiligenceApprovalPolicy(IPartyRepository parties, IDueDiligenceStore store, DueDiligenceService service, IClock clock, IAuditTrail audit) :
    IIntegrationEventHandler<Workflow.Contracts.ApprovalGranted>,
    IIntegrationEventHandler<Workflow.Contracts.ApprovalRejected>
{
    public Task Handle(Workflow.Contracts.ApprovalGranted granted, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Decide(granted.BusinessRefType, granted.BusinessRefId, granted.ApprovalType, true, granted.ApprovalId, granted.CheckerId, cancellationToken);

    public Task Handle(Workflow.Contracts.ApprovalRejected rejected, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Decide(rejected.BusinessRefType, rejected.BusinessRefId, rejected.ApprovalType, false, rejected.ApprovalId, rejected.CheckerId, cancellationToken);

    private async Task Decide(string businessRefType, Guid partyId, string approvalType, bool approved, Guid approvalId, Guid checkerId, CancellationToken cancellationToken)
    {
        if (businessRefType is not (PartyApprovals.DueDiligenceBusinessRefType or PartyApprovals.RiskOverrideBusinessRefType or PartyApprovals.UnblockBusinessRefType)
            || await store.ForParty(partyId, cancellationToken) is not { } dueDiligence
            || await parties.Get(partyId, cancellationToken) is not { } party)
        {
            return;
        }

        if (approved && approvalType == PartyApprovals.EnhancedDueDiligenceType && dueDiligence.EnhancedApplied(approvalId))
        {
            return;
        }

        var decided = approvalType switch
        {
            PartyApprovals.EnhancedDueDiligenceType => dueDiligence.DecideEnhanced(approved, approvalId, checkerId, clock.UtcNow),
            PartyApprovals.RiskClassOverrideType => dueDiligence.DecideOverride(approved, checkerId),
            PartyApprovals.UnblockType => dueDiligence.DecideUnblock(approved, checkerId),
            _ => Result.Success(),
        };
        if (decided.IsFailure)
        {
            throw new PrerequisiteMissingException(approved ? PrerequisiteCodes.ApprovalNotApplied : PrerequisiteCodes.ApprovalEndingNotApplied, $"{approvalType}:{approvalId}");
        }

        await service.Reassess(party, dueDiligence, null, [], cancellationToken);
        audit.Record(new AuditEntry("PARTY", approved ? "ApproveDueDiligence" : "RejectDueDiligence", nameof(DueDiligence), dueDiligence.Id.ToString(),
            new Dictionary<string, string?> { ["party"] = partyId.ToString(), ["type"] = approvalType, ["approval"] = approvalId.ToString(), ["status"] = dueDiligence.Status }));
    }
}

/// <summary>
/// The daily due diligence job for one tenant (§4.3, OQ-125): a cleared party past its review date expires (and Workflow
/// opens a review); a party screened against an older list version, or not screened for a month (the full re-screen), is
/// screened again and assessed.
/// </summary>
internal sealed class RunDueDiligenceJobsHandler(IPartyRepository parties, IDueDiligenceStore store, DueDiligenceService service, IPartnerDirectory partners,
    IScreeningAdapter screening, ITenantContext tenant, IBusinessDate businessDate, IClock clock, IAuditTrail audit) : ICommandHandler<RunDueDiligenceJobs, DueDiligenceJobRun>
{
    public async Task<Result<DueDiligenceJobRun>> Handle(RunDueDiligenceJobs command, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var partner = await partners.ActivePartner(tenantId, PartnerTypes.Screening, businessDate.Today, cancellationToken);
        var version = partner is null ? null : await screening.ListVersion(tenantId, partner, cancellationToken);
        var (expiring, rescreen) = await store.Due(businessDate.Today, version, businessDate.StartOf(businessDate.Today.AddMonths(-1)), cancellationToken);
        var expired = 0;
        foreach (var partyId in expiring)
        {
            if (await store.ForParty(partyId, cancellationToken) is { } dueDiligence && dueDiligence.Expire(businessDate.Today, clock.UtcNow))
            {
                expired++;
            }
        }

        var rescreened = 0;
        foreach (var partyId in partner is null ? [] : rescreen)
        {
            if (await parties.Get(partyId, cancellationToken) is { } party)
            {
                await service.ScreenAndAssess(party, null, cancellationToken);
                rescreened++;
            }
        }

        audit.Record(new AuditEntry("PARTY", "RunDueDiligenceJobs", nameof(DueDiligence), tenantId.ToString(), new Dictionary<string, string?>
        {
            ["expired"] = expired.ToString(CultureInfo.InvariantCulture), ["rescreened"] = rescreened.ToString(CultureInfo.InvariantCulture),
        }));
        return new DueDiligenceJobRun(expired, rescreened);
    }
}
