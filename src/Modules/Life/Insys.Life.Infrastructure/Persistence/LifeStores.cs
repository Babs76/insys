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
using System.Globalization;
using System.Text.RegularExpressions;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Contracts.Approvals;
using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Life.Application;
using Insys.Life.Contracts;
using Insys.Life.Domain.Claims;
using Insys.Life.Domain.Groups;
using Insys.Life.Domain.Policies;
using Insys.Life.Domain.Proposals;
using Insys.BuildingBlocks.Application.Context;
using Insys.Life.Domain.Underwriting;
using Insys.Life.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;

namespace Insys.Life.Infrastructure.Persistence;

/// <summary>
/// Life's records, each checked against the caller's scope for the endpoint's permission as it is loaded (F103,
/// addenda/access-scopes.md §4): outside it, a read finds nothing (404) and an action is refused (403 OUT_OF_SCOPE).
/// Underwriting cases are scoped as their proposal, and loans as their policy. Jobs and consumers are unrestricted.
/// </summary>
internal sealed class LifeRepository(LifeDbContext db, IExecutionContext execution) : ILifeRepository
{
    public async Task<LifeProposal?> Proposal(Guid id, CancellationToken cancellationToken) =>
        Scoped(await db.Proposals.Include(p => p.Lives).Include(p => p.Covers).Include(p => p.Disclosures).AsSplitQuery()
            .SingleOrDefaultAsync(p => p.Id == id, cancellationToken));

    public async Task<LifeUnderwritingCase?> Case(Guid id, CancellationToken cancellationToken) =>
        await OfProposal(await Cases().SingleOrDefaultAsync(c => c.Id == id, cancellationToken), cancellationToken);

    public async Task<LifeUnderwritingCase?> CaseForProposal(Guid proposalId, CancellationToken cancellationToken) =>
        db.ChangeTracker.Entries<LifeUnderwritingCase>().Select(e => e.Entity).FirstOrDefault(c => c.ProposalId == proposalId)
        ?? await OfProposal(await Cases().SingleOrDefaultAsync(c => c.ProposalId == proposalId, cancellationToken), cancellationToken);

    public async Task<LifePolicy?> Policy(Guid id, CancellationToken cancellationToken) => Scoped(await Policies().SingleOrDefaultAsync(p => p.Id == id, cancellationToken));

    public async Task<LifePolicy?> PolicyForProposal(Guid proposalId, CancellationToken cancellationToken) =>
        Scoped(await Policies().SingleOrDefaultAsync(p => p.ProposalId == proposalId, cancellationToken));

    public async Task<IReadOnlyList<Guid>> PoliciesOnAccount(Guid billingAccountRef, CancellationToken cancellationToken) =>
        await db.Policies.AsNoTracking()
            .Where(p => p.BillingAccountRef == billingAccountRef && (p.Status == LifePolicyStatus.Issued || p.Status == LifePolicyStatus.InForce))
            .OrderBy(p => p.Id).Select(p => p.Id).ToListAsync(cancellationToken);

    public async Task<LifePolicyLoan?> Loan(Guid id, CancellationToken cancellationToken)
    {
        var loan = await db.Loans.Include(l => l.Transactions).SingleOrDefaultAsync(l => l.Id == id, cancellationToken);
        if (loan is null)
        {
            return null;
        }

        var owner = await db.Policies.AsNoTracking().Where(p => p.Id == loan.PolicyId).Select(p => new { p.ProductCode, p.OrgNodeCode }).SingleAsync(cancellationToken);
        return execution.InScope(loan, owner.ProductCode, owner.OrgNodeCode);
    }

    public async Task<IReadOnlyList<LifePolicyLoan>> ActiveLoans(Guid policyId, CancellationToken cancellationToken) =>
        await db.Loans.Include(l => l.Transactions).Where(l => l.PolicyId == policyId && l.Status == LoanStatus.Active).OrderBy(l => l.Id).ToListAsync(cancellationToken);

    public Task<int> LoanCount(Guid policyId, CancellationToken cancellationToken) => db.Loans.CountAsync(l => l.PolicyId == policyId, cancellationToken);

    public async Task<decimal?> CashValue(Guid policyId, DateOnly asOf, CancellationToken cancellationToken) =>
        await db.CashValues.AsNoTracking().Where(v => v.PolicyId == policyId && v.ValuationDate <= asOf).OrderByDescending(v => v.ValuationDate)
            .Select(v => (decimal?)v.Value).FirstOrDefaultAsync(cancellationToken);

    public async Task<LifeClaim?> Claim(Guid id, CancellationToken cancellationToken) => Scoped(await Claims().SingleOrDefaultAsync(c => c.Id == id, cancellationToken));

    public async Task<LifeClaim?> ClaimByNumber(string claimNumber, CancellationToken cancellationToken) =>
        Scoped(await Claims().SingleOrDefaultAsync(c => c.ClaimNumber == claimNumber, cancellationToken));

    public Task<bool> OpenClaimExists(Guid policyId, string claimType, DateOnly eventDate, Guid exceptClaimId, CancellationToken cancellationToken) =>
        db.Claims.AnyAsync(c => c.PolicyId == policyId && c.ClaimType == claimType && c.EventDate == eventDate && c.Id != exceptClaimId
                                && c.Status != LifeClaimStatus.Notified && c.Status != LifeClaimStatus.Declined && c.Status != LifeClaimStatus.Closed, cancellationToken);

    public async Task<GroupScheme?> Scheme(Guid id, CancellationToken cancellationToken) =>
        Scoped(await db.Schemes.Include(s => s.Members).SingleOrDefaultAsync(s => s.Id == id, cancellationToken));

    public async Task<IReadOnlyList<Guid>> PoliciesToStart(DateOnly day, int limit, CancellationToken cancellationToken) =>
        await db.Policies.AsNoTracking().Where(p => p.Status == LifePolicyStatus.Issued && p.InceptionDate <= day).OrderBy(p => p.Id).Select(p => p.Id).Take(limit)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> PoliciesPastGrace(DateOnly day, int limit, CancellationToken cancellationToken) =>
        await db.Policies.AsNoTracking().Where(p => p.Status == LifePolicyStatus.InForce && p.Schedules.Any(s => s.GraceUntil != null && s.GraceUntil < day))
            .OrderBy(p => p.Id).Select(p => p.Id).Take(limit).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> LapsedPolicies(int limit, CancellationToken cancellationToken) =>
        await db.Policies.AsNoTracking().Where(p => p.Status == LifePolicyStatus.Lapsed).OrderBy(p => p.Id).Select(p => p.Id).Take(limit).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> SchemesToActivate(DateOnly day, int limit, CancellationToken cancellationToken) =>
        await db.Schemes.AsNoTracking().Where(s => s.Status == SchemeStatus.SetUp && s.StartDate <= day).OrderBy(s => s.Id).Select(s => s.Id).Take(limit)
            .ToListAsync(cancellationToken);

    public void Add(LifeProposal proposal) => db.Proposals.Add(proposal);

    public void Add(LifeUnderwritingCase underwritingCase) => db.Cases.Add(underwritingCase);

    public void Add(LifePolicy policy) => db.Policies.Add(policy);

    public void Add(LifePolicyLoan loan) => db.Loans.Add(loan);

    public void Add(LifeClaim claim) => db.Claims.Add(claim);

    public void Add(GroupScheme scheme) => db.Schemes.Add(scheme);

    private LifeProposal? Scoped(LifeProposal? proposal) => execution.InScope(proposal, proposal?.ProductCode, proposal?.OrgNodeCode);

    private LifePolicy? Scoped(LifePolicy? policy) => execution.InScope(policy, policy?.ProductCode, policy?.OrgNodeCode);

    private LifeClaim? Scoped(LifeClaim? claim) => execution.InScope(claim, claim?.ProductCode, claim?.OrgNodeCode);

    private GroupScheme? Scoped(GroupScheme? scheme) => execution.InScope(scheme, scheme?.ProductCode, scheme?.OrgNodeCode);

    private async Task<LifeUnderwritingCase?> OfProposal(LifeUnderwritingCase? underwritingCase, CancellationToken cancellationToken)
    {
        if (underwritingCase is null)
        {
            return null;
        }

        var owner = await db.Proposals.AsNoTracking().Where(p => p.Id == underwritingCase.ProposalId).Select(p => new { p.ProductCode, p.OrgNodeCode })
            .SingleAsync(cancellationToken);
        return execution.InScope(underwritingCase, owner.ProductCode, owner.OrgNodeCode);
    }

    private IQueryable<LifeUnderwritingCase> Cases() => db.Cases.Include(c => c.Requirements).Include(c => c.Items).AsSplitQuery();

    private IQueryable<LifePolicy> Policies() =>
        db.Policies.Include(p => p.Lives).Include(p => p.Benefits).Include(p => p.Beneficiaries).Include(p => p.Schedules).Include(p => p.Events)
            .Include(p => p.LapseHistory).AsSplitQuery();

    private IQueryable<LifeClaim> Claims() => db.Claims.Include(c => c.Lines).Include(c => c.Payees).AsSplitQuery();
}

internal sealed partial class LifeCodes(LifeDbContext db) : ILifeCodes
{
    public async Task<string?> PolicyNumberPrefix(DateOnly day, CancellationToken cancellationToken) =>
        (await InForce<PolicyNumberingSchemes>(day).ToListAsync(cancellationToken))
        .Where(s => s.ProductLine.Equals("LIFE", StringComparison.OrdinalIgnoreCase)).Select(s => s.Prefix).FirstOrDefault();

    public async Task<NominationRule?> NominationRule(string productCode, DateOnly day, CancellationToken cancellationToken) =>
        await InForce<NominationRules>(day).Where(r => r.ProductCode == productCode)
            .Select(r => new NominationRule(r.MaxBeneficiaries, r.MinShare, r.AllowedRelationships)).FirstOrDefaultAsync(cancellationToken);

    public async Task<GraceRule?> GraceRule(string frequency, DateOnly day, CancellationToken cancellationToken) =>
        await InForce<GracePeriodRules>(day).Where(r => r.Frequency == frequency).Select(r => new GraceRule(r.GraceDays, r.CoverDuringGrace))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<LapseRule?> LapseRule(string productCode, DateOnly day, CancellationToken cancellationToken) =>
        await InForce<LapseAndReinstatementRules>(day).Where(r => r.ProductCode == productCode)
            .Select(r => new LapseRule(r.MissedPaymentsToLapse, r.ReinstatementWindow, r.EvidenceRequired)).FirstOrDefaultAsync(cancellationToken);

    public async Task<AlterationType?> AlterationType(string code, DateOnly day, CancellationToken cancellationToken) =>
        await InForce<AlterationTypes>(day).Where(t => t.AlterationCode == code)
            .Select(t => new AlterationType(t.AlterationCode, t.AllowedStates, t.ReUnderwriting, t.AuthorityRole, t.DocumentType)).FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<string, bool>> DecisionCodes(DateOnly day, CancellationToken cancellationToken) =>
        (await InForce<UnderwritingDecisionCodes>(day).Select(c => new { c.Code, c.RequiresTerms }).ToListAsync(cancellationToken))
        .GroupBy(c => c.Code).ToDictionary(g => g.Key, g => g.First().RequiresTerms);

    public Task<bool> PaysLumpSum(string benefitType, DateOnly day, CancellationToken cancellationToken) =>
        InForce<LifeBenefitTypes>(day).AnyAsync(t => t.Code == benefitType && t.PayoutType == "LUMP_SUM", cancellationToken);

    public async Task<string?> ClaimBenefitType(string claimType, DateOnly day, CancellationToken cancellationToken) =>
        await InForce<LifeClaimTypes>(day).Where(t => t.Code == claimType).Select(t => t.BenefitType).FirstOrDefaultAsync(cancellationToken);

    public async Task<LoanRule?> LoanRule(string productCode, DateOnly day, CancellationToken cancellationToken) =>
        await InForce<LoanEligibilityLimits>(day).Where(r => r.ProductCode == productCode).Select(r => new LoanRule(r.MaxPctCashValue, r.MinPolicyDuration))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<MemberCategoryDefinition?> MemberCategory(string schemeNumber, string categoryCode, DateOnly day, CancellationToken cancellationToken) =>
        await InForce<MemberCategoryDefinitions>(day).Where(c => c.Scheme == schemeNumber && c.CategoryCode == categoryCode)
            .Select(c => new MemberCategoryDefinition(c.CategoryCode, c.EligibilityRule, c.BenefitMultiple)).FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// The band whose range holds the member count. size_band is an int4range: the validator accepts interval notation
    /// ("[50,200)", "[200,)") and PostgreSQL stores it so, which is the one format read here (GAP-043). Before, this read
    /// "50-199 members", which no stored band could be, so no member ever had a free cover limit.
    /// </summary>
    public async Task<decimal?> FreeCoverLimit(int members, DateOnly day, CancellationToken cancellationToken) =>
        (await InForce<FreeCoverLimits>(day).ToListAsync(cancellationToken))
        .Where(b => StaticConverters.IntRangeContains(b.SizeBand, members))
        .Select(b => (decimal?)b.FreeCoverLimit).FirstOrDefault();

    public Task<bool> Exists(LifeCode kind, string code, DateOnly day, CancellationToken cancellationToken) => kind switch
    {
        LifeCode.CauseOfEvent => InForce<CauseOfEventCodes>(day).AnyAsync(c => c.Code == code, cancellationToken),
        LifeCode.BeneficiaryRelationship => InForce<BeneficiaryRelationshipTypes>(day).AnyAsync(c => c.Code == code, cancellationToken),
        LifeCode.LoanRate => InForce<PolicyLoanRates>(day).AnyAsync(c => c.RateCode == code, cancellationToken),
        _ => InForce<GroupSchemeTypes>(day).AnyAsync(c => c.Code == code, cancellationToken),
    };


    private IQueryable<T> InForce<T>(DateOnly day) where T : StaticRecord =>
        db.Set<T>().AsNoTracking().InForce(LifeStaticData.Descriptors.Single(d => d.EntityType == typeof(T)).Lifecycle, day);
}

/// <summary>The merge values of CLAIM_DECISION_RIGHTS (OQ-102): business references and amounts, invariant, and the reason's customer wording.</summary>
internal static class DecisionRightsMerge
{
    public static IReadOnlyDictionary<string, string> Of(string claimNumber, DateOnly decidedOn, string? reasons, decimal amount, decimal claimed, string currency)
    {
        var merge = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["claim_number"] = claimNumber,
            ["decision_date"] = decidedOn.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            ["amount"] = amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            ["claimed_amount"] = claimed.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            ["currency"] = currency,
        };
        if (!string.IsNullOrWhiteSpace(reasons))
        {
            merge["decision_reasons"] = reasons;
        }

        return merge;
    }
}

internal sealed class LifeEventMapper : IDomainEventMapper
{
    public IIntegrationEvent? Map(IDomainEvent domainEvent) => domainEvent switch
    {
        Domain.Proposals.LifeProposalSubmitted e => new Contracts.LifeProposalSubmitted(e.ProposalId, e.ProposalNumber, e.ProductCode, e.ProductVersion),
        Domain.Proposals.LifeProposalWithdrawn e => new Contracts.LifeProposalWithdrawn(e.ProposalId, e.ProposalNumber),
        Domain.Proposals.LifeProposalAccepted e => new Contracts.LifeProposalAccepted(e.ProposalId, e.ProposalNumber),
        Domain.Proposals.LifeProposalDeclined e => new Contracts.LifeProposalDeclined(e.ProposalId, e.ProposalNumber, e.Postponed),
        Domain.Underwriting.LifeUnderwritingCaseOpened e => new Contracts.LifeUnderwritingCaseOpened(e.CaseId, e.ProposalId),
        UnderwritingDecisionRequested e => new ApprovalRequested(LifeApprovals.UnderwritingBusinessRefType, e.CaseId, LifeApprovals.UnderwritingApprovalType,
            e.RequestedBy, e.SumAssured, e.Currency, $"Underwriting decision on proposal {e.ProposalId}", e.SubjectHash),
        Domain.Underwriting.LifeUnderwritingDecided e => new Contracts.LifeUnderwritingDecided(e.CaseId, e.ProposalId, e.Outcome.ToString().ToUpperInvariant(),
            e.DecisionCode, e.RulesetCode, e.RulesetVersion),
        Domain.Policies.LifePolicyIssued e => new Contracts.LifePolicyIssued(e.PolicyId, e.PolicyNumber, e.ProposalId, e.ProductCode, e.ProductVersion,
            e.PolicyholderPartyId, e.Billing.PayerPartyId, e.Billing.BillingAccountRef, e.Billing.Frequency, e.Billing.CollectionMethod, e.Currency, e.Premium,
            e.InceptionDate, e.TermEndDate, e.SumAssured, e.OrgNodeCode),
        Domain.Policies.LifePolicyChanged e => new Contracts.LifePolicyChanged(e.PolicyId, e.PolicyNumber, e.ChangeType, e.EffectiveDate),
        Domain.Policies.LifePolicyLapsed e => new Contracts.LifePolicyLapsed(e.PolicyId, e.PolicyNumber, e.LapseDate, e.MissedPayments, e.ProductCode, e.ProductVersion,
            e.Currency),
        Domain.Policies.LifePolicyReinstated e => new Contracts.LifePolicyReinstated(e.PolicyId, e.PolicyNumber, e.ReinstatedOn, e.TermEndDate),
        Domain.Policies.PolicyLoanIssued e => new Contracts.PolicyLoanIssued(e.LoanId, e.LoanNumber, e.PolicyId, e.Principal, e.Currency),
        Domain.Policies.PolicyLoanRepaid e => new Contracts.PolicyLoanRepaid(e.LoanId, e.LoanNumber, e.PolicyId),
        Domain.Claims.LifeClaimRegistered e => new Contracts.LifeClaimRegistered(e.ClaimId, e.ClaimNumber, e.PolicyId, e.ClaimType, e.EventDate),
        LifeClaimApprovalRequested e => new ApprovalRequested(LifeApprovals.ClaimBusinessRefType, e.ClaimId, LifeApprovals.ClaimApprovalType, e.RequestedBy, e.Amount,
            e.Currency, $"Life claim {e.ClaimNumber}", e.SubjectHash),
        Domain.Claims.LifeClaimApproved e => new Contracts.LifeClaimApproved(e.ClaimId, e.ClaimNumber, e.PolicyId, e.Amount, e.LoanOffset, e.Currency, e.ReserveType,
            e.Payees.Select(p => new Contracts.LifeClaimPayee(p.PartyId, p.BankAccountId, p.BankAccountHash, p.Amount)).ToList(), e.ApprovalId, e.CheckerId),
        Domain.Claims.LifeClaimDeclined e => new Contracts.LifeClaimDeclined(e.ClaimId, e.ClaimNumber, e.DecisionReason),
        Domain.Claims.LifeClaimDecisionRightsDue e => new Communications.Contracts.NotificationRequested(Communications.Contracts.NotificationTypes.ClaimDecisionRights,
            e.RecipientPartyId, "LIFE_CLAIM", e.ClaimId, DecisionRightsMerge.Of(e.ClaimNumber, e.DecidedOn, e.DecisionReasons, e.Amount, e.ClaimedAmount, e.Currency),
            e.ProductCode, e.OrgNodeCode),
        Domain.Claims.LifeClaimSettled e => new Contracts.LifeClaimSettled(e.ClaimId, e.ClaimNumber, e.PolicyId, e.PolicyBenefitIds, e.Amount, e.Currency, e.PaidOn),
        Domain.Groups.GroupMemberEnrolled e => new Contracts.GroupMemberEnrolled(e.SchemeId, e.SchemeNumber, e.MemberId, e.PartyId, e.CategoryCode, e.CoverAmount,
            e.EvidenceRequired),
        Domain.Groups.GroupSchemeRenewed e => new Contracts.GroupSchemeRenewed(e.SchemeId, e.SchemeNumber, e.RenewalDate),
        _ => null,
    };
}
