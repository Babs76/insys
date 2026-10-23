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
using Insys.BuildingBlocks.Domain;
using Insys.Life.Domain;
using Insys.Life.Domain.Claims;
using Insys.Life.Domain.Groups;
using Insys.Life.Domain.Policies;
using Insys.Life.Domain.Proposals;
using Insys.Life.Domain.Underwriting;
using Insys.ProductFactory.Contracts;

namespace Insys.Life.Application;

public interface ILifeRepository
{
    Task<LifeProposal?> Proposal(Guid id, CancellationToken cancellationToken);

    Task<LifeUnderwritingCase?> Case(Guid id, CancellationToken cancellationToken);

    Task<LifeUnderwritingCase?> CaseForProposal(Guid proposalId, CancellationToken cancellationToken);

    Task<LifePolicy?> Policy(Guid id, CancellationToken cancellationToken);

    Task<LifePolicy?> PolicyForProposal(Guid proposalId, CancellationToken cancellationToken);

    /// <summary>Policies billed on the account that can be in arrears (issued or in force).</summary>
    Task<IReadOnlyList<Guid>> PoliciesOnAccount(Guid billingAccountRef, CancellationToken cancellationToken);

    Task<LifePolicyLoan?> Loan(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<LifePolicyLoan>> ActiveLoans(Guid policyId, CancellationToken cancellationToken);

    Task<int> LoanCount(Guid policyId, CancellationToken cancellationToken);

    /// <summary>The latest cash value on or before the day (life.cash_value). TODO(spec): no valuation writes it yet (OQ-052).</summary>
    Task<decimal?> CashValue(Guid policyId, DateOnly asOf, CancellationToken cancellationToken);

    Task<LifeClaim?> Claim(Guid id, CancellationToken cancellationToken);

    Task<LifeClaim?> ClaimByNumber(string claimNumber, CancellationToken cancellationToken);

    /// <summary>Another open claim on the policy for the same claim type and event (CLM-005).</summary>
    Task<bool> OpenClaimExists(Guid policyId, string claimType, DateOnly eventDate, Guid exceptClaimId, CancellationToken cancellationToken);

    Task<GroupScheme?> Scheme(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Guid>> PoliciesToStart(DateOnly day, int limit, CancellationToken cancellationToken);

    /// <summary>Policies in force whose grace period ended before the day.</summary>
    Task<IReadOnlyList<Guid>> PoliciesPastGrace(DateOnly day, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<Guid>> LapsedPolicies(int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<Guid>> SchemesToActivate(DateOnly day, int limit, CancellationToken cancellationToken);

    void Add(LifeProposal proposal);

    void Add(LifeUnderwritingCase underwritingCase);

    void Add(LifePolicy policy);

    void Add(LifePolicyLoan loan);

    void Add(LifeClaim claim);

    void Add(GroupScheme scheme);
}

/// <summary>A scheme's member category with its eligibility rule (life.cfg_member_category_definitions).</summary>
public sealed record MemberCategoryDefinition(string CategoryCode, string EligibilityRule, decimal BenefitMultiple);

public enum LifeCode
{
    CauseOfEvent,
    BeneficiaryRelationship,
    LoanRate,
    GroupSchemeType,
}

/// <summary>The Life module's static data in force.</summary>
public interface ILifeCodes
{
    /// <summary>The policy number prefix (life.cfg_policy_numbering_schemes, product line LIFE).</summary>
    Task<string?> PolicyNumberPrefix(DateOnly day, CancellationToken cancellationToken);

    Task<NominationRule?> NominationRule(string productCode, DateOnly day, CancellationToken cancellationToken);

    Task<GraceRule?> GraceRule(string frequency, DateOnly day, CancellationToken cancellationToken);

    Task<LapseRule?> LapseRule(string productCode, DateOnly day, CancellationToken cancellationToken);

    Task<AlterationType?> AlterationType(string code, DateOnly day, CancellationToken cancellationToken);

    /// <summary>The underwriting decision codes in force, and whether each requires terms.</summary>
    Task<IReadOnlyDictionary<string, bool>> DecisionCodes(DateOnly day, CancellationToken cancellationToken);

    /// <summary>The benefit type a claim type pays (life.ref_life_claim_types).</summary>
    Task<string?> ClaimBenefitType(string claimType, DateOnly day, CancellationToken cancellationToken);

    Task<LoanRule?> LoanRule(string productCode, DateOnly day, CancellationToken cancellationToken);

    Task<MemberCategoryDefinition?> MemberCategory(string schemeNumber, string categoryCode, DateOnly day, CancellationToken cancellationToken);

    /// <summary>The free cover limit for a scheme of this many members (life.cfg_free_cover_limits, by size band).</summary>
    Task<decimal?> FreeCoverLimit(int members, DateOnly day, CancellationToken cancellationToken);

    Task<bool> Exists(LifeCode kind, string code, DateOnly day, CancellationToken cancellationToken);
}

internal static class Users
{
    public static Result<Guid> Require(IExecutionContext execution) =>
        execution.ActorType == ActorType.User && Guid.TryParse(execution.ActorId, out var id)
            ? id
            : Error.Forbidden(ErrorCodes.Forbidden, "This is done by an identified user.");
}

/// <summary>Product Factory's product versions as Life sees them.</summary>
internal static class Products
{
    public static async Task<Result<LifeProductTerms>> Sellable(ISellableProducts products, Guid tenantId, string productCode, int? version, DateOnly day,
        CancellationToken cancellationToken) =>
        await products.Sellable(tenantId, productCode, version, day, cancellationToken) is { Domain: "LIFE" } product
            ? new LifeProductTerms(product.ProductCode, product.Version, product.Coverages)
            : Error.Validation(LifeErrorCodes.ProductNotAvailable, $"{productCode}{(version is null ? "" : $" v{version}")} is not a Life product on sale on {day:yyyy-MM-dd}.");
}

internal static class Numbers
{
    /// <summary>TODO(spec): numbers are a prefix and an ID-based suffix; sequences and check digits need a counter (OQ-058).</summary>
    public static string Of(string prefix, Guid id, int length) => References.Of(prefix, id, length);

    public static async Task<Result<string>> Policy(ILifeCodes codes, Guid id, DateOnly day, CancellationToken cancellationToken) =>
        await codes.PolicyNumberPrefix(day, cancellationToken) is { } prefix
            ? Of(prefix, id, 15)
            : Error.Validation(LifeErrorCodes.CodeNotInForce, "No Life policy numbering scheme is in force.");
}

/// <summary>Age next birthday on a date, as Life rates and records entry ages.</summary>
internal static class Ages
{
    public static int NextBirthday(DateOnly birthDate, DateOnly on)
    {
        var age = on.Year - birthDate.Year;
        if (birthDate.AddYears(age) > on)
        {
            age--;
        }

        return age + 1;
    }
}

public sealed record InsuredLifeView(Guid Id, Guid PartyId, string LifeRole, int AgeNext, string SmokerStatus, string? OccupationCode);

public sealed record CoverView(Guid Id, Guid InsuredLifeId, string BenefitCode, decimal SumAssured, int TermYears, decimal Premium);

public sealed record ProposalView(Guid Id, string ProposalNumber, string ProductCode, int ProductVersion, Guid ApplicantPartyId, ProposalStatus Status, string Currency,
    string Frequency, DateOnly? InceptionDate, decimal? QuotedPremium, Guid? RatingRequestId, int? RatingVersion, string? EligibilityOutcome, string? RulesetCode,
    int? RulesetVersion, IReadOnlyList<InsuredLifeView> Lives, IReadOnlyList<CoverView> Covers, IReadOnlyList<BeneficiaryNomination> Nominations, long RowVersion)
{
    public static ProposalView From(LifeProposal p) => new(p.Id, p.ProposalNumber, p.ProductCode, p.ProductVersion, p.ApplicantPartyId, p.Status, p.Currency,
        p.Frequency, p.InceptionDate, p.QuotedPremium, p.RatingRequestId, p.RatingVersion, p.EligibilityOutcome, p.RulesetCode, p.RulesetVersion,
        p.Lives.Select(l => new InsuredLifeView(l.Id, l.PartyId, l.LifeRole, l.AgeNext, l.SmokerStatus, l.OccupationCode)).ToList(),
        p.Covers.Select(c => new CoverView(c.Id, c.InsuredLifeId, c.BenefitCode, c.SumAssured, c.TermYears, c.Premium)).ToList(), p.BeneficiaryNominations, p.RowVersion);
}

public sealed record RequirementView(Guid Id, string EvidenceType, [property: OneOf("REQUESTED", "RECEIVED", "WAIVED")] string Status, bool Mandatory, Guid? DocumentId, Guid? WaivedBy, string? WaiverReason);

public sealed record CaseView(Guid Id, Guid ProposalId, CaseStatus Status, string? RulesetCode, int? RulesetVersion, string? RuleOutcome, string? Outcome, string? DecisionCode,
    string? DecisionReason, bool DecisionPending, Guid? DecidedBy, string? AuthorityRole, string? ReferredTo, IReadOnlyList<RequirementView> Requirements,
    IReadOnlyList<DecisionItemRequest> Items, long RowVersion)
{
    public static CaseView From(LifeUnderwritingCase c) => new(c.Id, c.ProposalId, c.Status, c.RulesetCode, c.RulesetVersion, c.RuleOutcome, c.Outcome?.ToString(),
        c.DecisionCode, c.DecisionReason, c.DecisionPending, c.DecidedBy, c.AuthorityRole, c.ReferredTo,
        c.Requirements.Select(r => new RequirementView(r.Id, r.EvidenceType, r.Status, r.Mandatory, r.DocumentId, r.WaivedBy, r.WaiverReason)).ToList(),
        c.Items.Select(i => i.ToRequest()).ToList(), c.RowVersion);
}

public sealed record BenefitView(Guid Id, Guid? InsuredLifeId, string BenefitCode, decimal SumAssured, decimal Premium, IReadOnlyList<string> ExclusionCodes,
    DateOnly StartDate, DateOnly? EndDate, [property: OneOf("ACTIVE", "CLAIMED", "ENDED")] string Status);

public sealed record BeneficiaryView(Guid PartyId, string RelationshipCode, decimal SharePct, DateOnly ValidFrom, DateOnly? ValidTo);

public sealed record ScheduleView(string Frequency, decimal Premium, DateOnly NextDueDate, int MissedCount, DateOnly? ArrearsSince, DateOnly? GraceUntil);

public sealed record LapseView(string EventType, DateOnly EffectiveDate, string Reason);

public sealed record PolicyView(Guid Id, string PolicyNumber, Guid ProposalId, string ProductCode, int ProductVersion, int RatingVersion, int? RulesetVersion,
    Guid PolicyholderPartyId, LifePolicyStatus Status, DateOnly InceptionDate, DateOnly? TermEndDate, string Currency, Guid BillingAccountRef, string? IntermediaryCode,
    IReadOnlyList<InsuredLifeView> Lives, IReadOnlyList<BenefitView> Benefits, IReadOnlyList<BeneficiaryView> Beneficiaries, ScheduleView Schedule,
    IReadOnlyList<LapseView> LapseHistory, long RowVersion)
{
    public static PolicyView From(LifePolicy p) => new(p.Id, p.PolicyNumber, p.ProposalId, p.ProductCode, p.ProductVersion, p.RatingVersion, p.RulesetVersion,
        p.PolicyholderPartyId, p.Status, p.InceptionDate, p.TermEndDate, p.Currency, p.BillingAccountRef, p.IntermediaryCode,
        p.Lives.Select(l => new InsuredLifeView(l.Id, l.PartyId, l.LifeRole, l.EntryAge, "", null)).ToList(),
        p.Benefits.Select(b => new BenefitView(b.Id, b.InsuredLifeId, b.BenefitCode, b.SumAssured, b.Premium, b.ExclusionCodes ?? [], b.StartDate, b.EndDate, b.Status)).ToList(),
        p.Beneficiaries.Select(b => new BeneficiaryView(b.PartyId, b.RelationshipCode, b.SharePct, b.ValidFrom, b.ValidTo)).ToList(),
        new ScheduleView(p.Schedule.Frequency, p.Schedule.Premium, p.Schedule.NextDueDate, p.Schedule.MissedCount, p.Schedule.ArrearsSince, p.Schedule.GraceUntil),
        p.LapseHistory.OrderBy(l => l.EffectiveDate).Select(l => new LapseView(l.EventType, l.EffectiveDate, l.Reason)).ToList(), p.RowVersion);
}

public sealed record LoanTransactionView(string Type, decimal Amount, DateOnly Date);

public sealed record LoanView(Guid Id, string LoanNumber, Guid PolicyId, decimal Principal, string RateCode, decimal Balance, LoanStatus Status, DateOnly IssuedAt,
    IReadOnlyList<LoanTransactionView> Transactions, long RowVersion)
{
    public static LoanView From(LifePolicyLoan l) => new(l.Id, l.LoanNumber, l.PolicyId, l.Principal, l.RateCode, l.Balance, l.Status, l.IssuedAt,
        l.Transactions.Select(t => new LoanTransactionView(t.TxnType, t.Amount, t.TxnDate)).ToList(), l.RowVersion);
}

public sealed record ClaimLineView(Guid PolicyBenefitId, decimal Amount);

public sealed record ClaimPayeeView(Guid PartyId, Guid BankAccountId, decimal SharePct, decimal Amount);

public sealed record LifeClaimView(Guid Id, string? ClaimNumber, Guid PolicyId, string ClaimType, DateOnly EventDate, string? CauseCode, Guid ClaimantPartyId,
    Guid InsuredLifeId, LifeClaimStatus Status, string Currency, decimal? AssessedAmount, decimal? ApprovedAmount, decimal PaidAmount, string? DecisionReason,
    string? DecisionRationale, bool ApprovalPending, IReadOnlyList<ClaimLineView> Lines, IReadOnlyList<ClaimPayeeView> Payees, long RowVersion)
{
    public static LifeClaimView From(LifeClaim c) => new(c.Id, c.ClaimNumber, c.PolicyId, c.ClaimType, c.EventDate, c.CauseCode, c.ClaimantPartyId, c.InsuredLifeId,
        c.Status, c.Currency, c.AssessedAmount, c.ApprovedAmount, c.PaidAmount, c.DecisionReason, c.DecisionRationale, c.ApprovalPending,
        c.Lines.Select(l => new ClaimLineView(l.PolicyBenefitId, l.Amount)).ToList(),
        c.Payees.Select(p => new ClaimPayeeView(p.PartyId, p.BankAccountId, p.SharePct, p.Amount)).ToList(), c.RowVersion);
}

public sealed record MemberView(Guid Id, Guid PartyId, string CategoryCode, decimal? AnnualSalary, decimal CoverAmount, DateOnly JoinedOn, DateOnly? LeftOn,
    [property: OneOf("NOT_REQUIRED", "REQUIRED", "ACCEPTED")] string EvidenceStatus, decimal? FreeCoverLimit);

public sealed record SchemeView(Guid Id, string SchemeNumber, Guid EmployerPartyId, string ProductCode, int ProductVersion, string? SchemeType, SchemeStatus Status,
    DateOnly? StartDate, DateOnly RenewalDate, string? Currency, IReadOnlyList<MemberView> Members, long RowVersion)
{
    public static SchemeView From(GroupScheme s) => new(s.Id, s.SchemeNumber, s.EmployerPartyId, s.ProductCode, s.ProductVersion, s.SchemeType, s.Status,
        s.StartDate, s.RenewalDate, s.Currency,
        s.Members.Select(m => new MemberView(m.Id, m.PartyId, m.CategoryCode, m.AnnualSalary, m.CoverAmount, m.JoinedOn, m.LeftOn, m.EvidenceStatus, m.FreeCoverLimit))
            .ToList(), s.RowVersion);
}
