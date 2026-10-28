/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : General
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
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;
using Insys.General.Domain.Claims;
using Insys.General.Domain.Policies;
using Insys.General.Domain.Underwriting;

namespace Insys.General.Application;

/// <summary>A quote in the quote list (GAP-032); open it for its risks, coverages and referrals.</summary>
public sealed record QuoteSummary(Guid Id, string QuoteNumber, int QuoteVersion, string ProductCode, Guid ProposerPartyId, QuoteStatus Status, DateOnly ValidUntil,
    DateOnly PeriodStart, DateOnly PeriodEnd, decimal? Premium, string Currency);

/// <summary>A policy in the policy list (GAP-032).</summary>
public sealed record PolicySummary(Guid Id, string PolicyNumber, string ProductCode, Guid PolicyholderPartyId, PolicyStatus Status, DateOnly PeriodStart,
    DateOnly PeriodEnd, decimal AnnualPremium, string Currency);

/// <summary>A claim in the claim list (GAP-032). ClaimNumber is null until the claim is registered.</summary>
public sealed record ClaimSummary(Guid Id, string? ClaimNumber, Guid PolicyId, string CoverageCode, DateOnly LossDate, DateTimeOffset NotifiedAt, ClaimStatus Status);

/// <summary>Query ListQuotes: by proposer or quote number (every version of it), by ID with cursor paging.</summary>
public sealed record ListQuotes(Guid? ProposerPartyId, string? QuoteNumber, Guid? AfterId, int Limit) : IQuery<IReadOnlyList<QuoteSummary>>;

/// <summary>Query ListPolicies: by policyholder or policy number, by ID with cursor paging.</summary>
public sealed record ListPolicies(Guid? PolicyholderPartyId, string? PolicyNumber, Guid? AfterId, int Limit) : IQuery<IReadOnlyList<PolicySummary>>;

/// <summary>Query ListClaims: by policy or claim number, by ID with cursor paging.</summary>
public sealed record ListClaims(Guid? PolicyId, string? ClaimNumber, Guid? AfterId, int Limit) : IQuery<IReadOnlyList<ClaimSummary>>;

/// <summary>
/// Query ListRiskObjects (BR-034): by type, risk zone, the policy that insures them or their owner (BR-092), by ID with
/// cursor paging. A risk object belongs to no product, so it is not scoped itself; filtered by a policy, only a policy in
/// the caller's scope.
/// </summary>
public sealed record ListRiskObjects([property: CodeList(CodeLists.RiskObjectTypes)] string? ObjectType, [property: CodeList(CodeLists.RiskZones)] string? RiskZone,
    Guid? PolicyId, Guid? AfterId, int Limit, Guid? OwnerPartyId = null) : IQuery<IReadOnlyList<RiskObjectView>>;

/// <summary>Query ListProposals (BR-034): by quote or status, within the caller's scope through the proposal's quote.</summary>
public sealed record ListProposals(Guid? QuoteId, ProposalStatus? Status, Guid? AfterId, int Limit) : IQuery<IReadOnlyList<ProposalView>>;

/// <summary>Query ListPolicyEndorsements (BR-034): a policy's endorsements; the policy must be in the caller's scope.</summary>
public sealed record ListPolicyEndorsements(Guid PolicyId, Guid? AfterId, int Limit) : IQuery<IReadOnlyList<EndorsementView>>;

/// <summary>
/// Query ListAssessments (BR-034): loss assessments by claim, status or assessor, within the caller's scope through their
/// claim; with a claim, the claim must be in scope (404 otherwise).
/// </summary>
public sealed record ListAssessments(Guid? ClaimId, AssessmentStatus? Status, Guid? AssessorPartyId, Guid? AfterId, int Limit) : IQuery<IReadOnlyList<AssessmentView>>;

public interface IGeneralLists
{
    Task<IReadOnlyList<QuoteSummary>> Quotes(ListQuotes query, CancellationToken cancellationToken);

    Task<IReadOnlyList<PolicySummary>> Policies(ListPolicies query, CancellationToken cancellationToken);

    Task<IReadOnlyList<ClaimSummary>> Claims(ListClaims query, CancellationToken cancellationToken);

    Task<IReadOnlyList<RiskObject>> RiskObjects(ListRiskObjects query, CancellationToken cancellationToken);

    Task<IReadOnlyList<GeneralProposal>> Proposals(ListProposals query, CancellationToken cancellationToken);

    Task<IReadOnlyList<GeneralEndorsement>> Endorsements(Guid policyId, Guid? afterId, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<LossAssessment>> Assessments(ListAssessments query, CancellationToken cancellationToken);

    /// <summary>A claim's recoveries with their salvage, by ID after <paramref name="afterId"/> (BR-032); the caller checks the claim's scope.</summary>
    Task<IReadOnlyList<Domain.Claims.RecoveryCase>> Recoveries(Guid claimId, Guid? afterId, int limit, CancellationToken cancellationToken);
}

internal sealed class GeneralListHandlers(IGeneralLists lists, IGeneralRepository general) :
    IQueryHandler<ListQuotes, IReadOnlyList<QuoteSummary>>,
    IQueryHandler<ListPolicies, IReadOnlyList<PolicySummary>>,
    IQueryHandler<ListClaims, IReadOnlyList<ClaimSummary>>,
    IQueryHandler<ListRiskObjects, IReadOnlyList<RiskObjectView>>,
    IQueryHandler<ListProposals, IReadOnlyList<ProposalView>>,
    IQueryHandler<ListPolicyEndorsements, IReadOnlyList<EndorsementView>>,
    IQueryHandler<ListAssessments, IReadOnlyList<AssessmentView>>
{
    public async Task<Result<IReadOnlyList<RiskObjectView>>> Handle(ListRiskObjects query, CancellationToken cancellationToken) =>
        Result.Success<IReadOnlyList<RiskObjectView>>((await lists.RiskObjects(query, cancellationToken)).Select(RiskObjectView.From).ToList());

    public async Task<Result<IReadOnlyList<ProposalView>>> Handle(ListProposals query, CancellationToken cancellationToken) =>
        Result.Success<IReadOnlyList<ProposalView>>((await lists.Proposals(query, cancellationToken)).Select(ProposalView.From).ToList());

    public async Task<Result<IReadOnlyList<EndorsementView>>> Handle(ListPolicyEndorsements query, CancellationToken cancellationToken) =>
        await general.Policy(query.PolicyId, cancellationToken) is null
            ? Error.NotFound(ErrorCodes.NotFound, "The policy was not found.")
            : Result.Success<IReadOnlyList<EndorsementView>>(
                (await lists.Endorsements(query.PolicyId, query.AfterId, query.Limit, cancellationToken)).Select(EndorsementView.From).ToList());

    public async Task<Result<IReadOnlyList<AssessmentView>>> Handle(ListAssessments query, CancellationToken cancellationToken) =>
        query.ClaimId is { } claim && await general.Claim(claim, cancellationToken) is null
            ? Error.NotFound(ErrorCodes.NotFound, "The claim was not found.")
            : Result.Success<IReadOnlyList<AssessmentView>>((await lists.Assessments(query, cancellationToken)).Select(AssessmentView.From).ToList());

    public async Task<Result<IReadOnlyList<QuoteSummary>>> Handle(ListQuotes query, CancellationToken cancellationToken) =>
        Result.Success(await lists.Quotes(query, cancellationToken));

    public async Task<Result<IReadOnlyList<PolicySummary>>> Handle(ListPolicies query, CancellationToken cancellationToken) =>
        Result.Success(await lists.Policies(query, cancellationToken));

    public async Task<Result<IReadOnlyList<ClaimSummary>>> Handle(ListClaims query, CancellationToken cancellationToken) =>
        Result.Success(await lists.Claims(query, cancellationToken));
}
