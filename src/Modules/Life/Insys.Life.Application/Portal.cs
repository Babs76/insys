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
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;
using Insys.Life.Domain.Claims;
using Insys.Life.Domain.Policies;

namespace Insys.Life.Application;

// External-user access (docs/specs/addenda/external-user-access.md): a Life policy in the member and broker portals.
// A member sees a policy where they are the policyholder or an insured life; a broker, where its code (or a sub-broker's)
// is the policy's intermediary (§2). The view leaves out underwriting (loadings, decisions) and, except for the
// policyholder, the beneficiaries; a beneficiary who is not the policyholder sees nothing before a claim (decided at sign-off).

public sealed record PortalLifeBenefitView(string BenefitCode, decimal SumAssured, decimal Premium, DateOnly StartDate, DateOnly? EndDate, string Status);

public sealed record PortalBeneficiaryView(Guid PartyId, string RelationshipCode, decimal SharePct);

public sealed record PortalLifePolicyView(Guid Id, string PolicyNumber, string ProductCode, LifePolicyStatus Status, DateOnly InceptionDate, DateOnly? TermEndDate,
    string Currency, Guid PolicyholderPartyId, string? IntermediaryCode, IReadOnlyList<PortalLifeBenefitView> Benefits, IReadOnlyList<PortalBeneficiaryView>? Beneficiaries)
{
    public static PortalLifePolicyView From(LifePolicy p, bool withBeneficiaries) => new(p.Id, p.PolicyNumber, p.ProductCode, p.Status, p.InceptionDate, p.TermEndDate,
        p.Currency, p.PolicyholderPartyId, p.IntermediaryCode,
        p.Benefits.Select(b => new PortalLifeBenefitView(b.BenefitCode, b.SumAssured, b.Premium, b.StartDate, b.EndDate, b.Status)).ToList(),
        withBeneficiaries ? p.Beneficiaries.Where(b => b.ValidTo is null).Select(b => new PortalBeneficiaryView(b.PartyId, b.RelationshipCode, b.SharePct)).ToList() : null);
}

public sealed record GetPortalLifePolicy(Guid PolicyId) : IQuery<PortalLifePolicyView>;

/// <summary>The member's Life policies (policyholder or insured life) or the broker's (its codes and its sub-brokers'), BR-091.</summary>
public sealed record ListPortalLifePolicies(Guid? AfterId, int Limit) : IQuery<IReadOnlyList<PortalLifePolicyView>>;

internal sealed class PortalLifePolicyHandlers(ILifeRepository life, ILifePortalQueries queries, IExecutionContext execution) :
    IQueryHandler<GetPortalLifePolicy, PortalLifePolicyView>,
    IQueryHandler<ListPortalLifePolicies, IReadOnlyList<PortalLifePolicyView>>
{
    public async Task<Result<PortalLifePolicyView>> Handle(GetPortalLifePolicy query, CancellationToken cancellationToken)
    {
        var who = execution.External ?? throw new InvalidOperationException("A portal endpoint is used by external users only.");
        if (await life.Policy(query.PolicyId, cancellationToken) is not { } policy)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The policy was not found.");
        }

        var related = Holder(who, policy) || (who.Kind == ExternalSubject.Party && policy.Lives.Any(l => l.PartyId == who.PartyId)) || who.Brokers(policy.IntermediaryCode);
        return related ? PortalLifePolicyView.From(policy, Holder(who, policy)) : Error.NotFound(ErrorCodes.NotFound, "The policy was not found.");
    }

    public async Task<Result<IReadOnlyList<PortalLifePolicyView>>> Handle(ListPortalLifePolicies query, CancellationToken cancellationToken)
    {
        var who = execution.External ?? throw new InvalidOperationException("A portal endpoint is used by external users only.");
        return Result.Success<IReadOnlyList<PortalLifePolicyView>>(
            (await queries.Policies(who, query.AfterId, query.Limit, cancellationToken)).Select(p => PortalLifePolicyView.From(p, Holder(who, p))).ToList());
    }

    /// <summary>Beneficiaries are shown to the policyholder only (§4).</summary>
    private static bool Holder(ExternalSubject who, LifePolicy policy) => who.Kind == ExternalSubject.Party && policy.PolicyholderPartyId == who.PartyId;
}

/// <param name="ApprovedAmount">What is to be paid, once the claim is approved; nothing before.</param>
public sealed record PortalLifeClaimView(Guid Id, string? ClaimNumber, Guid PolicyId, string ClaimType, DateOnly EventDate, DateTimeOffset NotifiedAt, LifeClaimStatus Status,
    decimal? ApprovedAmount, decimal PaidAmount, string Currency)
{
    public static PortalLifeClaimView From(LifeClaim c) => new(c.Id, c.ClaimNumber, c.PolicyId, c.ClaimType, c.EventDate, c.NotifiedAt, c.Status,
        c.Status is LifeClaimStatus.Approved or LifeClaimStatus.Settled or LifeClaimStatus.Closed ? c.ApprovedAmount : null, c.PaidAmount, c.Currency);
}

public sealed record ListPortalLifeClaims(Guid? AfterId, int Limit) : IQuery<IReadOnlyList<PortalLifeClaimView>>;

public sealed record GetPortalLifeClaim(Guid ClaimId) : IQuery<PortalLifeClaimView>;

/// <summary>The portal's Life policies and claims, in the query so paging stays correct, within the caller's relationship and F103 scope.</summary>
public interface ILifePortalQueries
{
    Task<IReadOnlyList<LifePolicy>> Policies(ExternalSubject who, Guid? afterId, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<LifeClaim>> Claims(ExternalSubject who, Guid? afterId, int limit, CancellationToken cancellationToken);
}

/// <summary>
/// A Life claim in the member and broker portals (§2): a member sees a claim they made, or one on a policy they hold or
/// are a nominated beneficiary of; a broker, the claims on its policies. The view leaves out the assessment, the
/// reserve, the decision's reasons and rationale, the authority and the payees (§4).
/// </summary>
internal sealed class PortalLifeClaimHandlers(ILifeRepository life, ILifePortalQueries queries, IExecutionContext execution) :
    IQueryHandler<GetPortalLifeClaim, PortalLifeClaimView>,
    IQueryHandler<ListPortalLifeClaims, IReadOnlyList<PortalLifeClaimView>>
{
    public async Task<Result<PortalLifeClaimView>> Handle(GetPortalLifeClaim query, CancellationToken cancellationToken)
    {
        var who = execution.External ?? throw new InvalidOperationException("A portal endpoint is used by external users only.");
        return await life.Claim(query.ClaimId, cancellationToken) is { } claim && await life.Policy(claim.PolicyId, cancellationToken) is { } policy
            && Related(who, claim, policy)
            ? PortalLifeClaimView.From(claim)
            : Error.NotFound(ErrorCodes.NotFound, "The claim was not found.");
    }

    public async Task<Result<IReadOnlyList<PortalLifeClaimView>>> Handle(ListPortalLifeClaims query, CancellationToken cancellationToken)
    {
        var who = execution.External ?? throw new InvalidOperationException("A portal endpoint is used by external users only.");
        return Result.Success<IReadOnlyList<PortalLifeClaimView>>(
            (await queries.Claims(who, query.AfterId, query.Limit, cancellationToken)).Select(PortalLifeClaimView.From).ToList());
    }

    private static bool Related(ExternalSubject who, LifeClaim claim, LifePolicy policy) =>
        who.Kind == ExternalSubject.Party
            ? claim.ClaimantPartyId == who.PartyId || policy.PolicyholderPartyId == who.PartyId
              || policy.Beneficiaries.Any(b => b.PartyId == who.PartyId && b.ValidTo is null)
            : who.Brokers(policy.IntermediaryCode);
}
