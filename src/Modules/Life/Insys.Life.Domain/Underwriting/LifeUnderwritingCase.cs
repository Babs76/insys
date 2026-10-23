/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Life
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
using Insys.Life.Domain.Proposals;

namespace Insys.Life.Domain.Underwriting;

/// <summary>States from domain-model.json (life › LifeUnderwritingCase).</summary>
public enum CaseStatus
{
    Open,
    AwaitingEvidence,
    InAssessment,
    Referred,
    Decided,
}

public enum CaseTrigger
{
    RequestEvidence,
    EvidenceComplete,
    NoEvidenceNeeded,
    ReferCase,
    ReferralAnswered,
    DecideCase,
}

/// <summary>What the underwriter decides for the proposal as a whole. The decision code (life.ref_underwriting_decision_codes) is recorded with it.</summary>
public enum UnderwritingOutcome
{
    Accept,
    Decline,
    Postpone,
}

/// <summary>
/// Value object DecisionItem's terms for one benefit: a loading (percent or per mille), an exclusion, or a
/// postponement. A decision code that requires terms (requires_terms) needs at least one.
/// </summary>
public sealed record DecisionItemRequest(string BenefitCode, string DecisionCode, decimal? LoadingPct, decimal? LoadingPerMille, string? ExclusionCode,
    DateOnly? PostponedUntil)
{
    public bool HasTerms => LoadingPct is not null || LoadingPerMille is not null || ExclusionCode is not null || PostponedUntil is not null;

    public bool IsLoaded => LoadingPct is > 0 || LoadingPerMille is > 0;
}

public sealed record LifeUnderwritingCaseOpened(Guid CaseId, Guid ProposalId, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>Asks Workflow for a second person's approval of the decision, within their authority for the sum assured (§7.4, SRS-002).</summary>
public sealed record UnderwritingDecisionRequested(Guid CaseId, Guid ProposalId, Guid RequestedBy, decimal SumAssured, string Currency, string SubjectHash,
    DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record LifeUnderwritingDecided(Guid CaseId, Guid ProposalId, UnderwritingOutcome Outcome, string DecisionCode, string? RulesetCode, int? RulesetVersion,
    Guid DecidedBy, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>Entity UnderwritingRequirement (life.underwriting_requirement): evidence requested, received or waived.</summary>
public sealed class UnderwritingRequirement : Entity<Guid>, ITenantScoped
{
    public const string Requested = "REQUESTED";
    public const string Received = "RECEIVED";
    public const string Waived = "WAIVED";

    private UnderwritingRequirement()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid CaseId { get; private set; }

    public string EvidenceType { get; private set; } = "";

    public string Status { get; private set; } = Requested;

    public bool Mandatory { get; private set; }

    public Guid? DocumentId { get; private set; }

    public DateTimeOffset RequestedAt { get; private set; }

    public DateTimeOffset? ReceivedAt { get; private set; }

    public Guid? WaivedBy { get; private set; }

    public string? WaiverReason { get; private set; }

    public long RowVersion { get; private set; }

    public bool Outstanding => Mandatory && Status == Requested;

    internal static UnderwritingRequirement Create(Guid caseId, string evidenceType, bool mandatory, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(), CaseId = caseId, EvidenceType = Guard.MaxLength(evidenceType, 20), Mandatory = mandatory, RequestedAt = now,
    };

    internal void Receive(Guid documentId, DateTimeOffset now)
    {
        Status = Received;
        DocumentId = documentId;
        ReceivedAt = now;
    }

    internal void Waive(Guid by, string reason)
    {
        Status = Waived;
        WaivedBy = by;
        WaiverReason = Guard.MaxLength(reason, 200);
    }
}

/// <summary>Entity DecisionItem (life.underwriting_decision_item): the terms for one benefit.</summary>
public sealed class UnderwritingDecisionItem : Entity<Guid>, ITenantScoped
{
    private UnderwritingDecisionItem()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid CaseId { get; private set; }

    public string BenefitCode { get; private set; } = "";

    public string DecisionCode { get; private set; } = "";

    public decimal? LoadingPct { get; private set; }

    public decimal? LoadingPerMille { get; private set; }

    public string? ExclusionCode { get; private set; }

    public DateOnly? PostponedUntil { get; private set; }

    public long RowVersion { get; private set; }

    public DecisionItemRequest ToRequest() => new(BenefitCode, DecisionCode, LoadingPct, LoadingPerMille, ExclusionCode, PostponedUntil);

    internal static UnderwritingDecisionItem Create(Guid caseId, DecisionItemRequest request) => new()
    {
        Id = Guid.CreateVersion7(), CaseId = caseId, BenefitCode = Guard.MaxLength(request.BenefitCode, 20), DecisionCode = Guard.MaxLength(request.DecisionCode, 15),
        LoadingPct = request.LoadingPct, LoadingPerMille = request.LoadingPerMille, ExclusionCode = request.ExclusionCode, PostponedUntil = request.PostponedUntil,
    };
}

/// <summary>
/// The LifeUnderwritingCase aggregate (life.underwriting_case, underwriting_requirement, underwriting_decision_item).
/// It records the rule set version the proposal was checked with, and the underwriting rule set's outcome for the case,
/// so the decision can be reproduced (LIF-002). A final decision needs every mandatory piece of evidence received or
/// waived. It is the underwriter's alone when it matches the rule outcome and is within their authority matrix;
/// otherwise (an override, or above authority) a second person approves it in Workflow (§7.4, SRS-002). TODO(spec):
/// how a set of underwriting rules combines into an outcome follows OQ-051's proposal.
/// </summary>
public sealed class LifeUnderwritingCase : AggregateRoot<Guid>
{
    public static readonly StateMachine<CaseStatus, CaseTrigger> Lifecycle = new StateMachine<CaseStatus, CaseTrigger>()
        .Allow(CaseStatus.Open, CaseTrigger.RequestEvidence, CaseStatus.AwaitingEvidence)
        .Allow(CaseStatus.AwaitingEvidence, CaseTrigger.EvidenceComplete, CaseStatus.InAssessment)
        .Allow(CaseStatus.Open, CaseTrigger.NoEvidenceNeeded, CaseStatus.InAssessment)
        .Allow(CaseStatus.InAssessment, CaseTrigger.ReferCase, CaseStatus.Referred)
        .Allow(CaseStatus.Referred, CaseTrigger.ReferralAnswered, CaseStatus.InAssessment)
        .Allow(CaseStatus.InAssessment, CaseTrigger.DecideCase, CaseStatus.Decided);

    private readonly List<UnderwritingRequirement> _requirements = [];
    private readonly List<UnderwritingDecisionItem> _items = [];

    private LifeUnderwritingCase()
    {
    }

    public Guid ProposalId { get; private set; }

    public CaseStatus Status { get; private set; }

    public Guid? AssignedTo { get; private set; }

    public string? RulesetCode { get; private set; }

    public int? RulesetVersion { get; private set; }

    public UnderwritingOutcome? Outcome { get; private set; }

    public string? DecisionCode { get; private set; }

    public string? DecisionReason { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    public Guid? DecidedBy { get; private set; }

    public Guid? RequestedBy { get; private set; }

    public Guid? ApprovalId { get; private set; }

    public string? AuthorityRole { get; private set; }

    public string? ReferredTo { get; private set; }

    /// <summary>The underwriting rule set's outcome for the case (ACCEPT, REFER or DECLINE), from Rules.</summary>
    public string? RuleOutcome { get; private set; }

    public Guid? RuleDecisionId { get; private set; }

    public IReadOnlyList<UnderwritingRequirement> Requirements => _requirements;

    public IReadOnlyList<UnderwritingDecisionItem> Items => _items;

    /// <summary>A decision waiting for Workflow's approval.</summary>
    public bool DecisionPending => Status == CaseStatus.InAssessment && RequestedBy is not null;

    public string? SubjectHash => Outcome is null
        ? null
        : ApprovalSubject.Hash([
            "LIFE_UNDERWRITING", Id, Outcome, DecisionCode, DecisionReason,
            .. _items.OrderBy(i => i.BenefitCode, StringComparer.Ordinal)
                .SelectMany(i => new object?[] { i.BenefitCode, i.DecisionCode, i.LoadingPct, i.LoadingPerMille, i.ExclusionCode, i.PostponedUntil }),
        ]);

    /// <summary>Command OpenCase, for a submitted proposal, with the rule set version it was checked with.</summary>
    public static Result<LifeUnderwritingCase> OpenCase(Guid id, LifeProposal proposal, DateTimeOffset now)
    {
        if (proposal.Status != ProposalStatus.Submitted)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"An underwriting case is opened for a submitted proposal; {proposal.ProposalNumber} is {proposal.Status}.");
        }

        var opened = new LifeUnderwritingCase
        {
            Id = id, ProposalId = proposal.Id, Status = CaseStatus.Open, RulesetCode = proposal.RulesetCode, RulesetVersion = proposal.RulesetVersion,
        };
        opened.Raise(new LifeUnderwritingCaseOpened(id, proposal.Id, now));
        return opened;
    }

    public void Assign(Guid underwriter)
    {
        AssignedTo = underwriter;
        MarkChanged();
    }

    /// <summary>The underwriting rule set's decision for the case (Rules' RuleDecisionRecorded); the last before the decision applies.</summary>
    public Result RecordRuleOutcome(Guid decisionId, string outcome)
    {
        if (Status == CaseStatus.Decided || DecisionPending)
        {
            return Error.Conflict(LifeErrorCodes.DecisionPending, "The case is decided or waiting for approval.");
        }

        RuleOutcome = Guard.MaxLength(outcome, 10);
        RuleDecisionId = decisionId;
        MarkChanged();
        return Result.Success();
    }

    /// <summary>Whether a decision matches the rule outcome: REFER, or no rule outcome, matches nothing (every decision then overrides).</summary>
    public bool MatchesRuleOutcome(UnderwritingOutcome outcome) => RuleOutcome is { } rule && rule.Equals(outcome.ToString(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Command RequestEvidence: the case waits for it. Evidence is requested before assessment starts.</summary>
    public Result<UnderwritingRequirement> RequestEvidence(string evidenceType, bool mandatory, DateTimeOffset now)
    {
        if (Status == CaseStatus.Open)
        {
            var result = Fire(CaseTrigger.RequestEvidence);
            if (result.IsFailure)
            {
                return result.Error;
            }
        }
        else if (Status != CaseStatus.AwaitingEvidence)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Evidence is requested while the case is open or awaiting evidence; it is {Status}.");
        }

        var requirement = UnderwritingRequirement.Create(Id, evidenceType, mandatory, now);
        _requirements.Add(requirement);
        MarkChanged();
        return requirement;
    }

    /// <summary>Command RecordEvidence: once nothing mandatory is outstanding, assessment starts ("Evidence complete").</summary>
    public Result RecordEvidence(Guid requirementId, Guid documentId, DateTimeOffset now)
    {
        if (Requirement(requirementId) is not { Status: UnderwritingRequirement.Requested } requirement)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "That evidence is not outstanding.");
        }

        requirement.Receive(documentId, now);
        return CompleteIfReady();
    }

    /// <summary>Waives evidence; the API allows it only to someone with the waiver permission (authority).</summary>
    public Result WaiveEvidence(Guid requirementId, string reason, Guid by)
    {
        if (Requirement(requirementId) is not { Status: UnderwritingRequirement.Requested } requirement || string.IsNullOrWhiteSpace(reason))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "Only outstanding evidence is waived, with a reason.");
        }

        requirement.Waive(by, reason);
        return CompleteIfReady();
    }

    /// <summary>Transition "No evidence needed".</summary>
    public Result StartAssessment() => Fire(CaseTrigger.NoEvidenceNeeded);

    public Result ReferCase(string toRole)
    {
        if (DecisionPending)
        {
            return Error.Conflict(LifeErrorCodes.DecisionPending, "A decision is waiting for approval.");
        }

        var result = Fire(CaseTrigger.ReferCase);
        if (result.IsSuccess)
        {
            ReferredTo = Guard.MaxLength(toRole, 30);
        }

        return result;
    }

    /// <summary>Transition "Referral answered".</summary>
    public Result AnswerReferral() => Fire(CaseTrigger.ReferralAnswered);

    /// <summary>
    /// Command DecideCase, as the maker: the decision and its terms per benefit go to Workflow for a second person's
    /// approval within authority for the sum assured. Terms are needed where the decision code requires them.
    /// </summary>
    public Result RequestDecision(UnderwritingOutcome outcome, string decisionCode, string reason, IReadOnlyList<DecisionItemRequest> items,
        IReadOnlySet<string> codesRequiringTerms, decimal sumAssured, string currency, Guid by, DateTimeOffset now)
    {
        var valid = Validate(decisionCode, items, codesRequiringTerms);
        if (valid.IsFailure)
        {
            return valid;
        }

        Apply(outcome, decisionCode, reason, items, by);
        Raise(new UnderwritingDecisionRequested(Id, ProposalId, by, sumAssured, currency, SubjectHash!, now));
        return Result.Success();
    }

    /// <summary>
    /// Command DecideCase by one underwriter: allowed when the decision matches the rule outcome and is within their
    /// authority matrix (checked by the application); audited like any decision.
    /// </summary>
    public Result DecideCase(UnderwritingOutcome outcome, string decisionCode, string reason, IReadOnlyList<DecisionItemRequest> items,
        IReadOnlySet<string> codesRequiringTerms, Guid by, string authorityRole, DateTimeOffset now)
    {
        if (!MatchesRuleOutcome(outcome))
        {
            return Error.Conflict(LifeErrorCodes.DecisionPending, "A decision that differs from the rule outcome needs a second person's approval.");
        }

        var valid = Validate(decisionCode, items, codesRequiringTerms);
        if (valid.IsFailure)
        {
            return valid;
        }

        Apply(outcome, decisionCode, reason, items, by);
        var result = Fire(CaseTrigger.DecideCase);
        if (result.IsSuccess)
        {
            DecidedBy = by;
            DecidedAt = now;
            AuthorityRole = Guard.MaxLength(authorityRole, 30);
            Raise(new LifeUnderwritingDecided(Id, ProposalId, outcome, DecisionCode!, RulesetCode, RulesetVersion, by, now));
        }

        return result;
    }

    private Result Validate(string decisionCode, IReadOnlyList<DecisionItemRequest> items, IReadOnlySet<string> codesRequiringTerms)
    {
        if (Status != CaseStatus.InAssessment || DecisionPending)
        {
            return Error.Conflict(DecisionPending ? LifeErrorCodes.DecisionPending : ErrorCodes.InvalidStateTransition,
                DecisionPending ? "A decision is already waiting for approval." : $"The case is {Status}.");
        }

        if (_requirements.Any(r => r.Outstanding))
        {
            return Error.Conflict(LifeErrorCodes.EvidenceOutstanding, "Mandatory evidence is outstanding; receive or waive it first.");
        }

        if (items.FirstOrDefault(i => codesRequiringTerms.Contains(i.DecisionCode) && !i.HasTerms) is { } bare)
        {
            return Error.Validation(LifeErrorCodes.TermsRequired, $"{bare.DecisionCode} on {bare.BenefitCode} needs a loading, exclusion or postponement.");
        }

        if (codesRequiringTerms.Contains(decisionCode) && items.Count == 0)
        {
            return Error.Validation(LifeErrorCodes.TermsRequired, $"{decisionCode} needs the terms for each benefit.");
        }

        return Result.Success();
    }

    private void Apply(UnderwritingOutcome outcome, string decisionCode, string reason, IReadOnlyList<DecisionItemRequest> items, Guid by)
    {
        Outcome = outcome;
        DecisionCode = Guard.MaxLength(decisionCode, 15);
        DecisionReason = Guard.MaxLength(reason, 20);
        RequestedBy = by;
        _items.Clear();
        _items.AddRange(items.Select(i => UnderwritingDecisionItem.Create(Id, i)));
        MarkChanged();
    }

    /// <summary>Workflow's decision. Approved, the case is Decided; rejected, the request is withdrawn and assessment continues.</summary>
    public Result Decide(bool approved, Guid approvalId, Guid checkerId, string? subjectHash, string? authorityRole, DateTimeOffset now)
    {
        if (!DecisionPending)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "No decision is waiting for approval.");
        }

        if (subjectHash != SubjectHash)
        {
            return Error.Conflict(LifeErrorCodes.ApprovalSubjectChanged, "The approval was for another decision.");
        }

        if (checkerId == RequestedBy)
        {
            return MakerChecker.EnsureDifferentPeople(RequestedBy.ToString()!, checkerId.ToString());
        }

        if (!approved)
        {
            Outcome = null;
            DecisionCode = null;
            DecisionReason = null;
            RequestedBy = null;
            _items.Clear();
            MarkChanged();
            return Result.Success();
        }

        var result = Fire(CaseTrigger.DecideCase);
        if (result.IsSuccess)
        {
            ApprovalId = approvalId;
            DecidedBy = checkerId;
            DecidedAt = now;
            AuthorityRole = authorityRole;
            Raise(new LifeUnderwritingDecided(Id, ProposalId, Outcome!.Value, DecisionCode!, RulesetCode, RulesetVersion, checkerId, now));
        }

        return result;
    }

    private UnderwritingRequirement? Requirement(Guid id) => _requirements.SingleOrDefault(r => r.Id == id);

    private Result CompleteIfReady()
    {
        MarkChanged();
        return Status == CaseStatus.AwaitingEvidence && !_requirements.Any(r => r.Outstanding) ? Fire(CaseTrigger.EvidenceComplete) : Result.Success();
    }

    private Result Fire(CaseTrigger trigger)
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
