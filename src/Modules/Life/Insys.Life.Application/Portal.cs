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

internal sealed class GetPortalLifePolicyHandler(ILifeRepository life, IExecutionContext execution) : IQueryHandler<GetPortalLifePolicy, PortalLifePolicyView>
{
    public async Task<Result<PortalLifePolicyView>> Handle(GetPortalLifePolicy query, CancellationToken cancellationToken)
    {
        var who = execution.External ?? throw new InvalidOperationException("A portal endpoint is used by external users only.");
        if (await life.Policy(query.PolicyId, cancellationToken) is not { } policy)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The policy was not found.");
        }

        var holder = who.Kind == ExternalSubject.Party && policy.PolicyholderPartyId == who.PartyId;
        var related = holder || (who.Kind == ExternalSubject.Party && policy.Lives.Any(l => l.PartyId == who.PartyId)) || who.Brokers(policy.IntermediaryCode);
        return related ? PortalLifePolicyView.From(policy, holder) : Error.NotFound(ErrorCodes.NotFound, "The policy was not found.");
    }
}
