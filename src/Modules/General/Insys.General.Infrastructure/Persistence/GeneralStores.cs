using System.Text.Json;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.General.Application;
using Insys.General.Domain.Claims;
using Insys.General.Domain.Policies;
using Insys.General.Domain.Underwriting;
using Insys.General.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;

namespace Insys.General.Infrastructure.Persistence;

internal sealed class GeneralRepository(GeneralDbContext db) : IGeneralRepository
{
    public Task<RiskObject?> RiskObject(Guid id, CancellationToken cancellationToken) => db.RiskObjects.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);

    public Task<GeneralQuote?> Quote(Guid id, CancellationToken cancellationToken) => Quotes().SingleOrDefaultAsync(q => q.Id == id, cancellationToken);

    public Task<GeneralQuote?> LatestQuoteVersion(string quoteNumber, CancellationToken cancellationToken) =>
        Quotes().Where(q => q.QuoteNumber == quoteNumber).OrderByDescending(q => q.QuoteVersion).FirstOrDefaultAsync(cancellationToken);

    public Task<GeneralProposal?> Proposal(Guid id, CancellationToken cancellationToken) => db.Proposals.SingleOrDefaultAsync(p => p.Id == id, cancellationToken);

    public Task<GeneralProposal?> ProposalForQuote(Guid quoteId, CancellationToken cancellationToken) =>
        db.Proposals.SingleOrDefaultAsync(p => p.QuoteId == quoteId, cancellationToken);

    public Task<GeneralPolicy?> Policy(Guid id, CancellationToken cancellationToken) => Policies().SingleOrDefaultAsync(p => p.Id == id, cancellationToken);

    public Task<GeneralPolicy?> PolicyFromQuote(Guid quoteId, CancellationToken cancellationToken) =>
        Policies().FirstOrDefaultAsync(p => p.QuoteId == quoteId, cancellationToken);

    public Task<GeneralPolicy?> RenewalOf(Guid policyId, CancellationToken cancellationToken) =>
        Policies().FirstOrDefaultAsync(p => p.RenewalOfId == policyId, cancellationToken);

    public Task<GeneralEndorsement?> Endorsement(Guid id, CancellationToken cancellationToken) => db.Endorsements.SingleOrDefaultAsync(e => e.Id == id, cancellationToken);

    public Task<int> EndorsementCount(Guid policyId, CancellationToken cancellationToken) => db.Endorsements.CountAsync(e => e.PolicyId == policyId, cancellationToken);

    public Task<GeneralClaim?> Claim(Guid id, CancellationToken cancellationToken) =>
        db.Claims.Include(c => c.Parties).SingleOrDefaultAsync(c => c.Id == id, cancellationToken);

    public Task<GeneralClaim?> ClaimByNumber(string claimNumber, CancellationToken cancellationToken) =>
        db.Claims.Include(c => c.Parties).SingleOrDefaultAsync(c => c.ClaimNumber == claimNumber, cancellationToken);

    public Task<LossAssessment?> Assessment(Guid id, CancellationToken cancellationToken) =>
        db.Assessments.Include(a => a.Estimates).SingleOrDefaultAsync(a => a.Id == id, cancellationToken);

    public async Task<IReadOnlyList<LossAssessment>> Assessments(Guid claimId, CancellationToken cancellationToken)
    {
        var stored = await db.Assessments.Include(a => a.Estimates).Where(a => a.ClaimId == claimId).OrderBy(a => a.Id).ToListAsync(cancellationToken);
        var added = db.ChangeTracker.Entries<LossAssessment>().Where(e => e.State == EntityState.Added && e.Entity.ClaimId == claimId).Select(e => e.Entity);
        return stored.Concat(added).Distinct().ToList();
    }

    public Task<RecoveryCase?> Recovery(Guid id, CancellationToken cancellationToken) =>
        db.Recoveries.Include(r => r.Salvage).SingleOrDefaultAsync(r => r.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Guid>> PoliciesToStart(DateOnly day, int limit, CancellationToken cancellationToken) =>
        await db.Policies.AsNoTracking().Where(p => p.Status == PolicyStatus.Issued && p.PeriodStart <= day).OrderBy(p => p.Id).Select(p => p.Id).Take(limit)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> PoliciesToExpire(DateOnly day, int limit, CancellationToken cancellationToken) =>
        await db.Policies.AsNoTracking().Where(p => p.Status == PolicyStatus.InForce && p.PeriodEnd <= day).OrderBy(p => p.Id).Select(p => p.Id).Take(limit)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> QuotesToExpire(DateOnly day, int limit, CancellationToken cancellationToken) =>
        await db.Quotes.AsNoTracking().Where(q => q.Status == QuoteStatus.Issued && q.ValidUntil < day).OrderBy(q => q.Id).Select(q => q.Id).Take(limit)
            .ToListAsync(cancellationToken);

    public void Add(RiskObject riskObject) => db.RiskObjects.Add(riskObject);

    public void Add(GeneralQuote quote) => db.Quotes.Add(quote);

    public void Add(GeneralProposal proposal) => db.Proposals.Add(proposal);

    public void Add(GeneralPolicy policy) => db.Policies.Add(policy);

    public void Add(GeneralEndorsement endorsement) => db.Endorsements.Add(endorsement);

    public void Add(GeneralClaim claim) => db.Claims.Add(claim);

    public void Add(LossAssessment assessment) => db.Assessments.Add(assessment);

    public void Add(RecoveryCase recovery) => db.Recoveries.Add(recovery);

    private IQueryable<GeneralQuote> Quotes() =>
        db.Quotes.Include(q => q.Risks).ThenInclude(r => r.Coverages).Include(q => q.Referrals).AsSplitQuery();

    private IQueryable<GeneralPolicy> Policies() =>
        db.Policies.Include(p => p.Risks).ThenInclude(r => r.Coverages).Include(p => p.Cancellations).AsSplitQuery();
}

internal sealed class GeneralCodes(GeneralDbContext db) : IGeneralCodes
{
    public async Task<RiskObjectTypeDefinition?> RiskObjectType(string objectType, DateOnly day, CancellationToken cancellationToken)
    {
        var row = await InForce<RiskObjectTypesAndAttributeSchemas>(day).Where(t => t.ObjectType == objectType).OrderByDescending(t => t.Version).FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : new RiskObjectTypeDefinition(row.ObjectType, row.Version, Strings(row.Attributes), row.Validation is { } v ? Strings(v) : []);
    }

    public async Task<int?> QuoteValidityDays(string productCode, DateOnly day, CancellationToken cancellationToken) =>
        await InForce<QuoteValidityPeriods>(day).Where(q => q.ProductCode == productCode).Select(q => (int?)q.DaysValid).FirstOrDefaultAsync(cancellationToken);

    public async Task<string?> PolicyNumberPrefix(DateOnly day, CancellationToken cancellationToken) =>
        (await InForce<PolicyNumberingSchemes>(day).ToListAsync(cancellationToken))
        .Where(s => s.ProductLine.Equals("GENERAL", StringComparison.OrdinalIgnoreCase)).Select(s => s.Prefix).FirstOrDefault();

    public async Task<CancellationRule?> CancellationRule(string reasonCode, DateOnly day, CancellationToken cancellationToken) =>
        await InForce<CancellationReasonsAndRefundRules>(day).Where(r => r.ReasonCode == reasonCode)
            .Select(r => new CancellationRule(r.ReasonCode, r.Initiator, r.RefundBasis, r.Fee ?? 0)).FirstOrDefaultAsync(cancellationToken);

    public async Task<EndorsementType?> EndorsementType(string code, DateOnly day, CancellationToken cancellationToken) =>
        await InForce<EndorsementTypes>(day).Where(t => t.Code == code)
            .Select(t => new EndorsementType(t.Code, t.PremiumMethod, t.Documents, t.RequiresUnderwriting ?? false)).FirstOrDefaultAsync(cancellationToken);

    public async Task<RenewalRule?> RenewalRule(string productCode, DateOnly day, CancellationToken cancellationToken) =>
        await InForce<RenewalRules>(day).Where(r => r.ProductCode == productCode).Select(r => new RenewalRule(r.AutoRenew, r.ReRating)).FirstOrDefaultAsync(cancellationToken);

    public Task<bool> Exists(GeneralCode kind, string code, DateOnly day, CancellationToken cancellationToken) => kind switch
    {
        GeneralCode.ReferralReason => InForce<ReferralReasonCodes>(day).AnyAsync(r => r.Code == code, cancellationToken),
        GeneralCode.UnderwritingDecision => InForce<UnderwritingDecisionCodes>(day).AnyAsync(r => r.Code == code, cancellationToken),
        GeneralCode.LossCause => InForce<LossCauseCodes>(day).AnyAsync(r => r.CauseCode == code, cancellationToken),
        GeneralCode.Peril => InForce<PerilCodes>(day).AnyAsync(r => r.PerilCode == code, cancellationToken),
        GeneralCode.CatastropheEvent => InForce<CatastropheEventRegister>(day).AnyAsync(r => r.EventCode == code, cancellationToken),
        GeneralCode.RecoveryType => InForce<RecoveryTypes>(day).AnyAsync(r => r.Code == code, cancellationToken),
        GeneralCode.ValuationBasis => InForce<ValuationBases>(day).AnyAsync(r => r.Code == code, cancellationToken),
        _ => InForce<SalvageDisposalMethods>(day).AnyAsync(r => r.Code == code, cancellationToken),
    };

    public async Task<bool> OnPanel(Guid partyId, string line, bool repairer, DateOnly day, CancellationToken cancellationToken)
    {
        var party = partyId.ToString();
        var panelType = repairer ? "REPAIRER" : "ASSESSOR";
        return await InForce<RepairerAndAssessorPanels>(day).AnyAsync(p => p.Party == party && p.PanelType == panelType && p.Status == "ACTIVE" && p.ApprovedLines.Contains(line),
                   cancellationToken)
               || (!repairer && await InForce<AdjusterAndSurveyorPanels>(day).AnyAsync(p => p.Party == party && p.Status == "ACTIVE" && p.Lines.Contains(line), cancellationToken));
    }

    /// <summary>An attribute schema or validation list: a JSON array of strings (name:Type:Y|N, or an expression).</summary>
    private static string[] Strings(JsonElement json) =>
        json.ValueKind == JsonValueKind.Array ? json.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToArray() : [];

    private IQueryable<T> InForce<T>(DateOnly day) where T : StaticRecord =>
        db.Set<T>().AsNoTracking().InForce(GeneralStaticData.Descriptors.Single(d => d.EntityType == typeof(T)).Lifecycle, day);
}

internal sealed class GeneralEventMapper : IDomainEventMapper
{
    public IIntegrationEvent? Map(IDomainEvent domainEvent) => domainEvent switch
    {
        GeneralRiskObjectRegistered e => new Contracts.GeneralRiskObjectRegistered(e.RiskObjectId, e.ObjectType),
        GeneralQuoteIssued e => new Contracts.GeneralQuoteIssued(e.QuoteId, e.QuoteNumber, e.QuoteVersion, e.ProductCode, e.ProductVersion, e.Premium, e.Currency, e.ValidUntil),
        GeneralQuoteAccepted e => new Contracts.GeneralQuoteAccepted(e.QuoteId, e.QuoteNumber, e.QuoteVersion, e.ProposerPartyId),
        GeneralQuoteExpired e => new Contracts.GeneralQuoteExpired(e.QuoteId, e.QuoteNumber, e.QuoteVersion),
        GeneralPolicyIssued e => new Contracts.GeneralPolicyIssued(e.PolicyId, e.PolicyNumber, e.ProductCode, e.ProductVersion, e.PolicyholderPartyId, e.Billing.PayerPartyId,
            e.Billing.BillingAccountRef, e.Billing.Frequency, e.Billing.CollectionMethod, e.Currency, e.AnnualPremium, e.PeriodStart, e.PeriodEnd, e.RenewalOfId),
        GeneralPolicyEndorsed e => new Contracts.GeneralPolicyEndorsed(e.PolicyId, e.PolicyNumber, e.EndorsementId, e.EndorsementNumber, e.EffectiveDate, e.NewAnnualPremium,
            e.PremiumDelta, e.Currency, e.ProductCode, e.ProductVersion, e.DocumentTypes),
        GeneralPolicyRenewed e => new Contracts.GeneralPolicyRenewed(e.PolicyId, e.PolicyNumber, e.RenewalPolicyId),
        GeneralPolicyCancelled e => new Contracts.GeneralPolicyCancelled(e.PolicyId, e.PolicyNumber, e.ReasonCode, e.EffectiveDate, e.RefundAmount, e.Currency, e.ProductCode,
            e.ProductVersion),
        GeneralPolicyReinstated e => new Contracts.GeneralPolicyReinstated(e.PolicyId, e.PolicyNumber),
        GeneralEndorsementApproved e => new Contracts.GeneralEndorsementApproved(e.EndorsementId, e.PolicyId, e.ApprovalId),
        EndorsementSubmitted e => new BuildingBlocks.Contracts.Approvals.ApprovalRequested(Contracts.GeneralApprovals.EndorsementBusinessRefType, e.EndorsementId,
            Contracts.GeneralApprovals.EndorsementApprovalType, e.RequestedBy, Math.Abs(e.PremiumDelta), e.Currency, $"Endorsement {e.EndorsementNumber}"),
        GeneralClaimRegistered e => new Contracts.GeneralClaimRegistered(e.ClaimId, e.ClaimNumber, e.PolicyId, e.LossDate, e.PerilCode),
        AssessorAppointed e => new Contracts.AssessorAppointed(e.ClaimId, e.AssessorPartyId),
        SettlementRequested e => new BuildingBlocks.Contracts.Approvals.ApprovalRequested(Contracts.GeneralApprovals.SettlementBusinessRefType, e.ClaimId,
            Contracts.GeneralApprovals.SettlementApprovalType, e.RequestedBy, e.Amount, e.Currency, $"Settlement of claim {e.ClaimNumber}"),
        GeneralClaimApproved e => new Contracts.GeneralClaimApproved(e.ClaimId, e.ClaimNumber, e.PayeePartyId, e.Amount, e.Currency, e.ReserveType, e.ApprovalId, e.CheckerId),
        GeneralClaimSettled e => new Contracts.GeneralClaimSettled(e.ClaimId, e.ClaimNumber, e.Amount, e.Currency, e.PaidOn),
        GeneralClaimClosed e => new Contracts.GeneralClaimClosed(e.ClaimId, e.ClaimNumber),
        LossAssessed e => new Contracts.LossAssessed(e.AssessmentId, e.ClaimId, e.AssessedAmount, e.ExcessApplied),
        RecoveryRecorded e => new Contracts.RecoveryRecorded(e.RecoveryCaseId, e.ClaimId, e.ClaimNumber, e.RecoveryType, e.Amount, e.Currency, e.ReserveType),
        _ => null,
    };
}
