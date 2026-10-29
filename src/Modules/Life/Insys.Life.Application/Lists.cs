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
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;
using Insys.Life.Domain.Claims;
using Insys.Life.Domain.Groups;
using Insys.Life.Domain.Policies;
using Insys.Life.Domain.Proposals;
using Insys.Life.Domain.Underwriting;

namespace Insys.Life.Application;

// GAP-040: Life's record lists, by party, number and status, with cursor paging by ID. Open a record for its detail.

public sealed record LifeProposalSummary(Guid Id, string ProposalNumber, string ProductCode, Guid ApplicantPartyId, ProposalStatus Status, string Currency,
    decimal? QuotedPremium, DateOnly? InceptionDate);

/// <param name="ProposalId">The proposal the policy was issued from (BR-086).</param>
public sealed record LifePolicySummary(Guid Id, string PolicyNumber, string ProductCode, Guid PolicyholderPartyId, LifePolicyStatus Status, DateOnly InceptionDate,
    DateOnly? TermEndDate, string Currency, Guid ProposalId);

/// <summary>A policy loan in a list (BR-062), in its policy's currency (BR-063).</summary>
public sealed record LifeLoanSummary(Guid Id, string LoanNumber, Guid PolicyId, decimal Principal, decimal Balance, LoanStatus Status, DateOnly IssuedAt, string Currency);

/// <summary>An underwriting case in a list (BR-074). DecisionPending: a decision is proposed and waits for a second person.</summary>
public sealed record UnderwritingCaseSummary(Guid Id, Guid ProposalId, string ProposalNumber, CaseStatus Status, Guid? AssignedTo, bool DecisionPending,
    string? DecisionCode, Guid? RequestedBy);

/// <summary>ClaimNumber is null until the claim is registered.</summary>
public sealed record LifeClaimSummary(Guid Id, string? ClaimNumber, Guid PolicyId, string ClaimType, Guid ClaimantPartyId, DateOnly EventDate, LifeClaimStatus Status,
    decimal? ApprovedAmount, string Currency);

public sealed record GroupSchemeSummary(Guid Id, string SchemeNumber, Guid EmployerPartyId, string ProductCode, SchemeStatus Status, DateOnly? StartDate,
    DateOnly RenewalDate);

public sealed record ListLifeProposals(Guid? ApplicantPartyId, string? ProposalNumber, ProposalStatus? Status, Guid? AfterId, int Limit)
    : IQuery<IReadOnlyList<LifeProposalSummary>>;

public sealed record ListLifePolicies(Guid? PolicyholderPartyId, string? PolicyNumber, LifePolicyStatus? Status, Guid? AfterId, int Limit, Guid? ProposalId = null)
    : IQuery<IReadOnlyList<LifePolicySummary>>;

public sealed record ListPolicyLoans(Guid? PolicyId, LoanStatus? Status, Guid? AfterId, int Limit) : IQuery<IReadOnlyList<LifeLoanSummary>>;

/// <param name="UnderwriterId">The underwriter the case is assigned to.</param>
public sealed record ListUnderwritingCases(CaseStatus? Status, Guid? UnderwriterId, bool? DecisionPending, Guid? AfterId, int Limit)
    : IQuery<IReadOnlyList<UnderwritingCaseSummary>>;

public sealed record ListLifeClaims(Guid? PolicyId, Guid? ClaimantPartyId, string? ClaimNumber, LifeClaimStatus? Status, Guid? AfterId, int Limit)
    : IQuery<IReadOnlyList<LifeClaimSummary>>;

public sealed record ListGroupSchemes(Guid? EmployerPartyId, string? SchemeNumber, SchemeStatus? Status, Guid? AfterId, int Limit)
    : IQuery<IReadOnlyList<GroupSchemeSummary>>;

public interface ILifeLists
{
    Task<IReadOnlyList<LifeProposalSummary>> Proposals(ListLifeProposals query, CancellationToken cancellationToken);

    Task<IReadOnlyList<LifePolicySummary>> Policies(ListLifePolicies query, CancellationToken cancellationToken);

    Task<IReadOnlyList<LifeClaimSummary>> Claims(ListLifeClaims query, CancellationToken cancellationToken);

    Task<IReadOnlyList<GroupSchemeSummary>> Schemes(ListGroupSchemes query, CancellationToken cancellationToken);

    Task<IReadOnlyList<LifeLoanSummary>> Loans(ListPolicyLoans query, CancellationToken cancellationToken);

    Task<IReadOnlyList<UnderwritingCaseSummary>> Cases(ListUnderwritingCases query, CancellationToken cancellationToken);
}

internal sealed class LifeListHandlers(ILifeLists lists) :
    IQueryHandler<ListLifeProposals, IReadOnlyList<LifeProposalSummary>>,
    IQueryHandler<ListLifePolicies, IReadOnlyList<LifePolicySummary>>,
    IQueryHandler<ListLifeClaims, IReadOnlyList<LifeClaimSummary>>,
    IQueryHandler<ListGroupSchemes, IReadOnlyList<GroupSchemeSummary>>,
    IQueryHandler<ListPolicyLoans, IReadOnlyList<LifeLoanSummary>>,
    IQueryHandler<ListUnderwritingCases, IReadOnlyList<UnderwritingCaseSummary>>
{
    public async Task<Result<IReadOnlyList<LifeProposalSummary>>> Handle(ListLifeProposals query, CancellationToken cancellationToken) =>
        Result.Success(await lists.Proposals(query, cancellationToken));

    public async Task<Result<IReadOnlyList<LifePolicySummary>>> Handle(ListLifePolicies query, CancellationToken cancellationToken) =>
        Result.Success(await lists.Policies(query, cancellationToken));

    public async Task<Result<IReadOnlyList<LifeClaimSummary>>> Handle(ListLifeClaims query, CancellationToken cancellationToken) =>
        Result.Success(await lists.Claims(query, cancellationToken));

    public async Task<Result<IReadOnlyList<GroupSchemeSummary>>> Handle(ListGroupSchemes query, CancellationToken cancellationToken) =>
        Result.Success(await lists.Schemes(query, cancellationToken));

    public async Task<Result<IReadOnlyList<LifeLoanSummary>>> Handle(ListPolicyLoans query, CancellationToken cancellationToken) =>
        Result.Success(await lists.Loans(query, cancellationToken));

    public async Task<Result<IReadOnlyList<UnderwritingCaseSummary>>> Handle(ListUnderwritingCases query, CancellationToken cancellationToken) =>
        Result.Success(await lists.Cases(query, cancellationToken));
}
