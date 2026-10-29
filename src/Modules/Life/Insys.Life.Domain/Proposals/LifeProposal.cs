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
using Insys.Life.Domain.Underwriting;

namespace Insys.Life.Domain.Proposals;

/// <summary>States from domain-model.json (life › LifeProposal).</summary>
public enum ProposalStatus
{
    Draft,
    Quoted,
    Submitted,
    InUnderwriting,
    Accepted,
    Declined,
    Postponed,
    Withdrawn,
    Converted,
}

public enum ProposalTrigger
{
    QuotePremium,
    SubmitProposal,
    CaseOpened,
    DecisionAccept,
    DecisionDecline,
    DecisionPostpone,
    WithdrawProposal,
    PolicyIssued,
}

/// <summary>A Life product version on sale: its code, version and benefits (the product's coverages are Life benefit type codes).</summary>
public sealed record LifeProductTerms(string ProductCode, int Version, IReadOnlyList<string> Benefits);

public sealed record InsuredLifeRequest(Guid PartyId, string LifeRole, int AgeNext, string SmokerStatus, string? OccupationCode);

public sealed record CoverRequest(Guid InsuredPartyId, string BenefitCode, decimal SumAssured, int TermYears);

/// <param name="Answer">The answer as JSON text; the application checks it is JSON.</param>
public sealed record DisclosureRequest(Guid InsuredPartyId, string QuestionCode, int QuestionVersion, string Answer);

/// <summary>Value object BeneficiaryNomination: who is nominated, how related, and their share in percent.</summary>
public sealed record BeneficiaryNomination(Guid PartyId, [property: BuildingBlocks.Domain.CodeList("life.ref_beneficiary_relationship_types", "code")] string RelationshipCode,
    decimal SharePct);

/// <summary>A product's nomination rule (life.cfg_nomination_rules).</summary>
public sealed record NominationRule(int MaxBeneficiaries, decimal MinShare, IReadOnlyList<string> AllowedRelationships);

/// <summary>How a capture is billed once a policy is issued.</summary>
public sealed record ProposalBilling(Guid PayerPartyId, string Frequency, string CollectionMethod);

public sealed record LifeProposalSubmitted(Guid ProposalId, string ProposalNumber, string ProductCode, int ProductVersion, Guid ApplicantPartyId, DateTimeOffset OccurredAt)
    : IDomainEvent;

public sealed record LifeProposalWithdrawn(Guid ProposalId, string ProposalNumber, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record LifeProposalAccepted(Guid ProposalId, string ProposalNumber, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record LifeProposalDeclined(Guid ProposalId, string ProposalNumber, bool Postponed, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>Beneficiary nominations: shares total 100% (domain-model.json), within the product's nomination rule.</summary>
public static class Nominations
{
    public static Result Validate(IReadOnlyList<BeneficiaryNomination> nominations, NominationRule? rule)
    {
        if (nominations.Count == 0)
        {
            return Result.Success();
        }

        if (nominations.Any(n => n.SharePct <= 0) || nominations.Sum(n => n.SharePct) != 100m || nominations.Select(n => n.PartyId).Distinct().Count() != nominations.Count)
        {
            return Error.Validation(LifeErrorCodes.BeneficiarySharesInvalid, "Each beneficiary has one share above zero, and the shares total 100%.");
        }

        if (rule is not null && (nominations.Count > rule.MaxBeneficiaries || nominations.Any(n => n.SharePct < rule.MinShare)
                                 || (rule.AllowedRelationships.Count > 0 && nominations.Any(n => !rule.AllowedRelationships.Contains(n.RelationshipCode)))))
        {
            return Error.Validation(LifeErrorCodes.NominationRuleBreached,
                $"At most {rule.MaxBeneficiaries} beneficiaries, each with at least {rule.MinShare}% and an allowed relationship.");
        }

        return Result.Success();
    }
}

/// <summary>Entity InsuredLife (life.proposal_insured_life).</summary>
public sealed class ProposalInsuredLife : Entity<Guid>, ITenantScoped
{
    private ProposalInsuredLife()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid ProposalId { get; private set; }

    public Guid PartyId { get; private set; }

    public string LifeRole { get; private set; } = "";

    public int AgeNext { get; private set; }

    public string SmokerStatus { get; private set; } = "";

    public string? OccupationCode { get; private set; }

    public long RowVersion { get; private set; }

    internal static ProposalInsuredLife Create(Guid proposalId, InsuredLifeRequest request) => new()
    {
        Id = Guid.CreateVersion7(), ProposalId = proposalId, PartyId = Guard.NotEmpty(request.PartyId), LifeRole = Guard.MaxLength(request.LifeRole, 10),
        AgeNext = request.AgeNext, SmokerStatus = Guard.MaxLength(request.SmokerStatus, 10), OccupationCode = request.OccupationCode,
    };
}

/// <summary>Entity RequestedCover (life.proposal_cover): a benefit on an insured life. Its premium is its share of the quoted premium.</summary>
public sealed class RequestedCover : Entity<Guid>, ITenantScoped
{
    private RequestedCover()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid ProposalId { get; private set; }

    public Guid InsuredLifeId { get; private set; }

    public string BenefitCode { get; private set; } = "";

    public decimal SumAssured { get; private set; }

    public int TermYears { get; private set; }

    public decimal Premium { get; internal set; }

    public long RowVersion { get; private set; }

    internal static RequestedCover Create(Guid proposalId, Guid insuredLifeId, CoverRequest request) => new()
    {
        Id = Guid.CreateVersion7(), ProposalId = proposalId, InsuredLifeId = insuredLifeId, BenefitCode = Guard.MaxLength(request.BenefitCode, 20),
        SumAssured = request.SumAssured, TermYears = request.TermYears,
    };
}

/// <summary>Entity DisclosureAnswer (life.disclosure_answer): an answer to a versioned question.</summary>
public sealed class DisclosureAnswer : Entity<Guid>, ITenantScoped
{
    private DisclosureAnswer()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid ProposalId { get; private set; }

    public Guid InsuredLifeId { get; private set; }

    public string QuestionCode { get; private set; } = "";

    public int QuestionVersion { get; private set; }

    public string Answer { get; private set; } = "";

    public long RowVersion { get; private set; }

    internal static DisclosureAnswer Create(Guid proposalId, Guid insuredLifeId, DisclosureRequest request) => new()
    {
        Id = Guid.CreateVersion7(), ProposalId = proposalId, InsuredLifeId = insuredLifeId, QuestionCode = Guard.MaxLength(request.QuestionCode, 20),
        QuestionVersion = request.QuestionVersion, Answer = request.Answer,
    };
}

/// <summary>
/// The LifeProposal aggregate (life.proposal, proposal_insured_life, proposal_cover, disclosure_answer): the product
/// version on sale (PRD-007), the insured lives, their covers and disclosures, and the beneficiary nominations. It is
/// rated by Rating and checked by an eligibility rule set before submission (LIF-001); after submission its content is
/// frozen and only underwriting changes the terms.
/// </summary>
public sealed class LifeProposal : AggregateRoot<Guid>
{
    public const string Eligible = "ELIGIBLE";

    public static readonly StateMachine<ProposalStatus, ProposalTrigger> Lifecycle = new StateMachine<ProposalStatus, ProposalTrigger>()
        .Allow(ProposalStatus.Draft, ProposalTrigger.QuotePremium, ProposalStatus.Quoted)
        .Allow(ProposalStatus.Quoted, ProposalTrigger.SubmitProposal, ProposalStatus.Submitted)
        .Allow(ProposalStatus.Submitted, ProposalTrigger.CaseOpened, ProposalStatus.InUnderwriting)
        .Allow(ProposalStatus.InUnderwriting, ProposalTrigger.DecisionAccept, ProposalStatus.Accepted)
        .Allow(ProposalStatus.InUnderwriting, ProposalTrigger.DecisionDecline, ProposalStatus.Declined)
        .Allow(ProposalStatus.InUnderwriting, ProposalTrigger.DecisionPostpone, ProposalStatus.Postponed)
        .Allow(ProposalStatus.Quoted, ProposalTrigger.WithdrawProposal, ProposalStatus.Withdrawn)
        .Allow(ProposalStatus.Submitted, ProposalTrigger.WithdrawProposal, ProposalStatus.Withdrawn)
        .Allow(ProposalStatus.Accepted, ProposalTrigger.PolicyIssued, ProposalStatus.Converted);

    private readonly List<ProposalInsuredLife> _lives = [];
    private readonly List<RequestedCover> _covers = [];
    private readonly List<DisclosureAnswer> _disclosures = [];

    private LifeProposal()
    {
    }

    public string ProposalNumber { get; private set; } = "";

    /// <summary>The organisation node (normally a branch) that owns the record (F103, addenda/access-scopes.md §2); its children inherit it.</summary>
    public string? OrgNodeCode { get; private set; }

    /// <summary>Places a new record at its organisation node (§2), once.</summary>
    public void PlaceAt(string? orgNodeCode)
    {
        OrgNodeCode ??= orgNodeCode is null ? null : Guard.MaxLength(orgNodeCode, 15);
    }

    public string ProductCode { get; private set; } = "";

    public int ProductVersion { get; private set; }

    public Guid ApplicantPartyId { get; private set; }

    public string? IntermediaryCode { get; private set; }

    public string ChannelCode { get; private set; } = "";

    public ProposalStatus Status { get; private set; }

    public string Currency { get; private set; } = "";

    /// <summary>The premium frequency; Rating prices the premium for it.</summary>
    public string Frequency { get; private set; } = "";

    /// <summary>The premium per instalment at <see cref="Frequency"/>, from Rating.</summary>
    public decimal? QuotedPremium { get; private set; }

    /// <summary>The premium calculation (Rating's calculation id).</summary>
    public Guid? RatingRequestId { get; private set; }

    public int? RatingVersion { get; private set; }

    public DateTimeOffset? SubmittedAt { get; private set; }

    public DateOnly? InceptionDate { get; private set; }

    public Guid? PayerPartyId { get; private set; }

    public string? CollectionMethod { get; private set; }

    public Guid? EligibilityDecisionId { get; private set; }

    public string? EligibilityOutcome { get; private set; }

    public string? RulesetCode { get; private set; }

    public int? RulesetVersion { get; private set; }

    public List<BeneficiaryNomination> BeneficiaryNominations { get; private set; } = [];

    public IReadOnlyList<ProposalInsuredLife> Lives => _lives;

    public IReadOnlyList<RequestedCover> Covers => _covers;

    public IReadOnlyList<DisclosureAnswer> Disclosures => _disclosures;

    public ProposalBilling Billing => new(PayerPartyId ?? ApplicantPartyId, Frequency, CollectionMethod ?? "");

    public decimal TotalSumAssured => _covers.Sum(c => c.SumAssured);

    /// <summary>Command CaptureProposal: the product version on sale, the lives, their covers within the product's benefits, and nominations totalling 100%.</summary>
    public static Result<LifeProposal> CaptureProposal(Guid id, string proposalNumber, LifeProductTerms product, Guid applicantPartyId, string? intermediaryCode,
        string channelCode, string currency, DateOnly inceptionDate, ProposalBilling billing, IReadOnlyList<InsuredLifeRequest> lives, IReadOnlyList<CoverRequest> covers,
        IReadOnlyList<DisclosureRequest> disclosures, IReadOnlyList<BeneficiaryNomination> nominations, NominationRule? nominationRule)
    {
        if (lives.Count == 0 || covers.Count == 0 || lives.Select(l => l.PartyId).Distinct().Count() != lives.Count)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "A proposal has at least one insured life, each once, and at least one cover.");
        }

        if (covers.FirstOrDefault(c => !product.Benefits.Contains(c.BenefitCode)) is { } outside)
        {
            return Error.Validation(LifeErrorCodes.BenefitNotInProduct, $"{outside.BenefitCode} is not a benefit of {product.ProductCode} v{product.Version}.");
        }

        if (covers.Any(c => c.SumAssured <= 0 || c.TermYears <= 0) || covers.Concat<object>(disclosures).Any(c => c switch
            {
                CoverRequest cover => lives.All(l => l.PartyId != cover.InsuredPartyId),
                DisclosureRequest answer => lives.All(l => l.PartyId != answer.InsuredPartyId),
                _ => false,
            }))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "Covers have a positive sum assured and term, and covers and answers belong to an insured life on the proposal.");
        }

        var nominated = Nominations.Validate(nominations, nominationRule);
        if (nominated.IsFailure)
        {
            return nominated.Error;
        }

        var proposal = new LifeProposal
        {
            Id = id, ProposalNumber = Guard.MaxLength(proposalNumber, 15), ProductCode = product.ProductCode, ProductVersion = product.Version,
            ApplicantPartyId = Guard.NotEmpty(applicantPartyId), IntermediaryCode = intermediaryCode, ChannelCode = Guard.MaxLength(channelCode, 12),
            Status = ProposalStatus.Draft, Currency = currency, Frequency = Guard.MaxLength(billing.Frequency, 10), InceptionDate = inceptionDate,
            PayerPartyId = billing.PayerPartyId, CollectionMethod = billing.CollectionMethod, BeneficiaryNominations = [.. nominations],
        };
        foreach (var life in lives)
        {
            var insured = ProposalInsuredLife.Create(id, life);
            proposal._lives.Add(insured);
            proposal._covers.AddRange(covers.Where(c => c.InsuredPartyId == life.PartyId).Select(c => RequestedCover.Create(id, insured.Id, c)));
            proposal._disclosures.AddRange(disclosures.Where(d => d.InsuredPartyId == life.PartyId).Select(d => DisclosureAnswer.Create(id, insured.Id, d)));
        }

        proposal.MarkChanged();
        return proposal;
    }

    /// <summary>
    /// Command QuotePremium, from Rating's PremiumCalculated for this proposal. Rating gives one premium per proposal;
    /// it is spread over the covers in proportion to their sums assured, any rounding difference on the largest
    /// (OQ-052, as for General). A new calculation before submission replaces the last.
    /// </summary>
    public Result QuotePremium(Guid calculationId, string productCode, int productVersion, int ratingVersion, decimal premium, string currency)
    {
        if (Status is not (ProposalStatus.Draft or ProposalStatus.Quoted))
        {
            return Error.Conflict(LifeErrorCodes.ProposalFrozen, $"Proposal {ProposalNumber} is {Status}; its premium is fixed.");
        }

        if (productCode != ProductCode || productVersion != ProductVersion || currency != Currency || premium <= 0)
        {
            return Error.Validation(LifeErrorCodes.RatingMismatch, "The calculation is for another product version or currency.");
        }

        if (Status == ProposalStatus.Draft)
        {
            var result = Fire(ProposalTrigger.QuotePremium);
            if (result.IsFailure)
            {
                return result;
            }
        }

        QuotedPremium = premium;
        RatingRequestId = calculationId;
        RatingVersion = ratingVersion;
        Allocate(premium);
        MarkChanged();
        return Result.Success();
    }

    /// <summary>The eligibility rule set's decision for this proposal (Rules' RuleDecisionRecorded), kept so it can be reproduced (LIF-002).</summary>
    public Result RecordEligibility(Guid decisionId, string rulesetCode, int rulesetVersion, string outcome)
    {
        if (Status is not (ProposalStatus.Draft or ProposalStatus.Quoted))
        {
            return Error.Conflict(LifeErrorCodes.ProposalFrozen, $"Proposal {ProposalNumber} is {Status}; its eligibility is fixed.");
        }

        EligibilityDecisionId = decisionId;
        RulesetCode = rulesetCode;
        RulesetVersion = rulesetVersion;
        EligibilityOutcome = outcome;
        MarkChanged();
        return Result.Success();
    }

    /// <summary>Command SubmitProposal: quoted, and eligible by the rule set (LIF-001). Its content is frozen from now.</summary>
    public Result SubmitProposal(DateTimeOffset now)
    {
        if (Status == ProposalStatus.Quoted && EligibilityOutcome != Eligible)
        {
            return Error.Conflict(LifeErrorCodes.ProposalNotEligible,
                EligibilityOutcome is null ? "The proposal has no eligibility decision yet." : $"The eligibility decision is {EligibilityOutcome}.");
        }

        var result = Fire(ProposalTrigger.SubmitProposal);
        if (result.IsSuccess)
        {
            SubmittedAt = now;
            Raise(new LifeProposalSubmitted(Id, ProposalNumber, ProductCode, ProductVersion, ApplicantPartyId, now));
        }

        return result;
    }

    /// <summary>Transition "Case opened": an underwriting case was opened for the proposal.</summary>
    public Result OpenUnderwriting() => Status == ProposalStatus.InUnderwriting ? Result.Success() : Fire(ProposalTrigger.CaseOpened);

    /// <summary>The underwriting decision (LifeUnderwritingDecided): accepted, declined or postponed.</summary>
    public Result ApplyDecision(UnderwritingOutcome outcome, DateTimeOffset now)
    {
        var trigger = outcome switch
        {
            UnderwritingOutcome.Accept => ProposalTrigger.DecisionAccept,
            UnderwritingOutcome.Decline => ProposalTrigger.DecisionDecline,
            _ => ProposalTrigger.DecisionPostpone,
        };
        var result = Fire(trigger);
        if (result.IsSuccess)
        {
            Raise(outcome == UnderwritingOutcome.Accept
                ? new LifeProposalAccepted(Id, ProposalNumber, now)
                : new LifeProposalDeclined(Id, ProposalNumber, outcome == UnderwritingOutcome.Postpone, now));
        }

        return result;
    }

    public Result WithdrawProposal(DateTimeOffset now)
    {
        var result = Fire(ProposalTrigger.WithdrawProposal);
        if (result.IsSuccess)
        {
            Raise(new LifeProposalWithdrawn(Id, ProposalNumber, now));
        }

        return result;
    }

    /// <summary>Transition "Policy issued".</summary>
    public Result MarkConverted() => Status == ProposalStatus.Converted ? Result.Success() : Fire(ProposalTrigger.PolicyIssued);

    private void Allocate(decimal premium)
    {
        var total = TotalSumAssured;
        foreach (var cover in _covers)
        {
            cover.Premium = Math.Round(premium * cover.SumAssured / total, 2, MidpointRounding.AwayFromZero);
        }

        _covers.MaxBy(c => c.SumAssured)!.Premium += premium - _covers.Sum(c => c.Premium);
    }

    private Result Fire(ProposalTrigger trigger)
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
