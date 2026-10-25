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
using System.Globalization;
using System.Text.Json;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Domain;
using Insys.General.Domain;
using Insys.General.Domain.Claims;
using Insys.General.Domain.Policies;
using Insys.General.Domain.Underwriting;
using Insys.ProductFactory.Contracts;

namespace Insys.General.Application;

public interface IGeneralRepository
{
    /// <summary>Each risk object's zone and the zone's peril in force (F062); risk objects with no zone are left out.</summary>
    Task<IReadOnlyDictionary<Guid, (string Zone, string Peril)>> ZonesOf(IReadOnlyCollection<Guid> riskObjectIds, DateOnly day, CancellationToken cancellationToken);

    /// <summary>The sum insured of the risks of issued and in-force policies in the zone and peril, in the currency, on the day (F062).</summary>
    Task<decimal> LiveExposure(string zone, string peril, string currency, DateOnly day, CancellationToken cancellationToken);

    /// <summary>The settlements requested, approved or paid on the policy's coverage for losses in the period, other than the claim's (F062 §2).</summary>
    Task<decimal> SettledOnCoverage(Guid policyId, string coverage, DateOnly from, DateOnly to, Guid exceptClaimId, CancellationToken cancellationToken);

    /// <summary>The zone and peril exposures of the day's snapshot (general.exposure_snapshot).</summary>
    Task<IReadOnlyList<(string Zone, string Peril, decimal SumInsured, string Currency)>> Snapshot(DateOnly day, CancellationToken cancellationToken);

    /// <summary>
    /// Records the day's alert for a zone, peril and currency and publishes AggregationLimitApproached through General's
    /// outbox (F062 §1, GAP-051); false, with nothing recorded, when that alert was already raised for the day.
    /// </summary>
    Task<bool> RecordLimitApproached(AggregationAlertView alert, string? targetRole, CancellationToken cancellationToken);

    Task<RiskObject?> RiskObject(Guid id, CancellationToken cancellationToken);

    Task<GeneralQuote?> Quote(Guid id, CancellationToken cancellationToken);

    /// <summary>The latest version of a quote number.</summary>
    Task<GeneralQuote?> LatestQuoteVersion(string quoteNumber, CancellationToken cancellationToken);

    Task<GeneralProposal?> Proposal(Guid id, CancellationToken cancellationToken);

    Task<GeneralProposal?> ProposalForQuote(Guid quoteId, CancellationToken cancellationToken);

    Task<GeneralPolicy?> Policy(Guid id, CancellationToken cancellationToken);

    Task<GeneralPolicy?> PolicyFromQuote(Guid quoteId, CancellationToken cancellationToken);

    Task<GeneralPolicy?> RenewalOf(Guid policyId, CancellationToken cancellationToken);

    Task<GeneralEndorsement?> Endorsement(Guid id, CancellationToken cancellationToken);

    Task<int> EndorsementCount(Guid policyId, CancellationToken cancellationToken);

    Task<GeneralClaim?> Claim(Guid id, CancellationToken cancellationToken);

    Task<GeneralClaim?> ClaimByNumber(string claimNumber, CancellationToken cancellationToken);

    Task<LossAssessment?> Assessment(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<LossAssessment>> Assessments(Guid claimId, CancellationToken cancellationToken);

    Task<RecoveryCase?> Recovery(Guid id, CancellationToken cancellationToken);

    /// <summary>For the daily jobs: issued policies whose period has started, policies in force whose period has ended, and issued quotes past validity.</summary>
    Task<IReadOnlyList<Guid>> PoliciesToStart(DateOnly day, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<Guid>> PoliciesToExpire(DateOnly day, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<Guid>> QuotesToExpire(DateOnly day, int limit, CancellationToken cancellationToken);

    /// <summary>Policies of a product in force, not yet invited to renew, whose period ends on or before the date.</summary>
    Task<IReadOnlyList<Guid>> PoliciesToInvite(string productCode, DateOnly lastPeriodEnd, int limit, CancellationToken cancellationToken);

    /// <summary>Policies of the product in force, invited to renew and not renewed, whose period ends on <paramref name="periodEnd"/> (OQ-089).</summary>
    Task<IReadOnlyList<Guid>> PoliciesToRemind(string productCode, DateOnly periodEnd, int limit, CancellationToken cancellationToken);

    /// <summary>Policies of a product in force whose period has ended by the day and that have no renewal.</summary>
    Task<IReadOnlyList<Guid>> PoliciesToRenew(string productCode, DateOnly day, int limit, CancellationToken cancellationToken);

    /// <summary>Replaces the day's exposure snapshot; returns the rows written.</summary>
    Task<int> RefreshExposure(DateOnly day, DateTimeOffset now, CancellationToken cancellationToken);

    void Add(RiskObject riskObject);

    void Add(GeneralQuote quote);

    void Add(GeneralProposal proposal);

    void Add(GeneralPolicy policy);

    void Add(GeneralEndorsement endorsement);

    void Add(GeneralClaim claim);

    void Add(LossAssessment assessment);

    void Add(RecoveryCase recovery);
}

/// <summary>A risk object type's attribute schema as configured (general.cfg_risk_object_types_and_attribute_schemas).</summary>
public sealed record RiskObjectTypeDefinition(string ObjectType, int Version, IReadOnlyList<string> Attributes, IReadOnlyList<string> Validation);

/// <summary>A renewal rule (general.cfg_renewal_rules).</summary>
/// <summary>A product's renewal rule (general.cfg_renewal_rules): the notice for the invitation, whether it renews by itself, and whether it is re-rated.</summary>
/// <param name="ReminderDays">Days before the period ends to remind a policyholder who has not renewed (OQ-089); empty by default.</param>
public sealed record RenewalRule(string ProductCode, int NoticeDays, bool AutoRenew, bool ReRating, string InvitationTemplate, IReadOnlyList<int>? ReminderDays = null);

public enum GeneralCode
{
    ReferralReason,
    UnderwritingDecision,
    LossCause,
    Peril,
    CatastropheEvent,
    RecoveryType,
    ValuationBasis,
    SalvageDisposal,
}

/// <summary>The General module's static data in force.</summary>
public interface IGeneralCodes
{
    /// <summary>The zone and peril limits in force (F062).</summary>
    Task<IReadOnlyList<ZoneLimit>> ZoneLimits(DateOnly day, CancellationToken cancellationToken);

    /// <summary>The coverage's aggregate claims limit in force, if any (F062).</summary>
    Task<AggregateLimit?> AggregateLimit(string coverage, DateOnly day, CancellationToken cancellationToken);

    Task<RiskObjectTypeDefinition?> RiskObjectType(string objectType, DateOnly day, CancellationToken cancellationToken);

    Task<int?> QuoteValidityDays(string productCode, DateOnly day, CancellationToken cancellationToken);

    /// <summary>The policy number prefix (general.cfg_policy_numbering_schemes, product line General).</summary>
    Task<string?> PolicyNumberPrefix(DateOnly day, CancellationToken cancellationToken);

    Task<CancellationRule?> CancellationRule(string reasonCode, DateOnly day, CancellationToken cancellationToken);

    Task<EndorsementType?> EndorsementType(string code, DateOnly day, CancellationToken cancellationToken);

    Task<RenewalRule?> RenewalRule(string productCode, DateOnly day, CancellationToken cancellationToken);

    Task<IReadOnlyList<RenewalRule>> RenewalRules(DateOnly day, CancellationToken cancellationToken);

    Task<bool> Exists(GeneralCode kind, string code, DateOnly day, CancellationToken cancellationToken);

    /// <summary>An assessor (or repairer) on an active approved panel for the line (GEN-007).</summary>
    Task<bool> OnPanel(Guid partyId, string line, bool repairer, DateOnly day, CancellationToken cancellationToken);
}

internal static class Users
{
    public static Result<Guid> Require(IExecutionContext execution) =>
        execution.ActorType == ActorType.User && Guid.TryParse(execution.ActorId, out var id)
            ? id
            : Error.Forbidden(ErrorCodes.Forbidden, "This is done by an identified user.");
}

/// <summary>Product Factory's product versions as General sees them.</summary>
internal static class Products
{
    public static ProductTerms Terms(SellableProduct product) =>
        new(product.ProductCode, product.Version, product.Line, product.Coverages,
            product.Limits.Select(l => new CoverageLimit(l.ScheduleCode, l.CoverageCode, l.LimitType, l.Amount, l.Percentage, l.Rule, l.Currency)).ToList(),
            product.CoverStartsOn);

    public static async Task<Result<ProductTerms>> Sellable(ISellableProducts products, Guid tenantId, string productCode, int? version, DateOnly day,
        CancellationToken cancellationToken) =>
        await products.Sellable(tenantId, productCode, version, day, cancellationToken) is { Domain: "GENERAL" } product
            ? Terms(product)
            : Error.Validation(GeneralErrorCodes.ProductNotAvailable, $"{productCode}{(version is null ? "" : $" v{version}")} is not a General product on sale on {day:yyyy-MM-dd}.");

    /// <summary>The version a policy was issued on, whether or not it is still on sale.</summary>
    public static async Task<Result<ProductTerms>> Issued(ISellableProducts products, Guid tenantId, string productCode, int version, DateOnly day,
        CancellationToken cancellationToken) =>
        await products.Version(tenantId, productCode, version, day, cancellationToken) is { } product
            ? Terms(product)
            : Error.Validation(GeneralErrorCodes.ProductNotAvailable, $"{productCode} v{version} is not a published product version.");
}

/// <summary>Risk attributes from JSON: numbers as decimals, strings, booleans and nulls; nothing nested.</summary>
internal static class Attributes
{
    public static Result<IReadOnlyDictionary<string, object?>> Read(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object)
        {
            return Error.Validation(GeneralErrorCodes.AttributesInvalid, "Attributes are a JSON object.");
        }

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in json.EnumerateObject())
        {
            object? value = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number when property.Value.TryGetDecimal(out var d) => d,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => Invalid,
            };
            if (ReferenceEquals(value, Invalid))
            {
                return Error.Validation(GeneralErrorCodes.AttributesInvalid, $"{property.Name} is not a string, number, boolean or null.");
            }

            values[property.Name] = value;
        }

        return values;
    }

    private static readonly object Invalid = new();

    public static string Format(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

public sealed record CoverageView(string CoverageCode, decimal SumInsured, string? ExcessScheduleCode, decimal? Premium);

public sealed record RiskView(Guid RiskObjectId, decimal SumInsured, DateOnly? AttachedFrom, DateOnly? DetachedOn, IReadOnlyList<CoverageView> Coverages);

public sealed record RiskObjectView(Guid Id, string ObjectType, JsonElement Attributes, int AttributeSchemaVersion, decimal? Valuation, string? ValuationBasis,
    string? Subdivision, string? PostalCode, string? RiskZone, RiskObjectStatus Status, long RowVersion)
{
    public static RiskObjectView From(RiskObject r) => new(r.Id, r.ObjectType, JsonDocument.Parse(r.Attributes).RootElement.Clone(), r.AttributeSchemaVersion, r.Valuation,
        r.ValuationBasis, r.Subdivision, r.PostalCode, r.RiskZone, r.Status, r.RowVersion);
}

public sealed record ReferralView(Guid Id, string ReasonCode, [property: OneOf("OPEN", "APPROVED", "DECLINED")] string Status, Guid? ReferredBy, Guid? DecidedBy, string? DecisionCode, IReadOnlyList<string> SpecialConditions,
    string? DeclineReason);

public sealed record QuoteView(Guid Id, string QuoteNumber, int QuoteVersion, string ProductCode, int ProductVersion, Guid ProposerPartyId, string? IntermediaryCode,
    QuoteStatus Status, DateOnly ValidUntil, DateOnly PeriodStart, DateOnly PeriodEnd, decimal? Premium, string Currency, Guid? RatingRequestId, int? RatingVersion,
    IReadOnlyList<RiskView> Risks, IReadOnlyList<ReferralView> Referrals, long RowVersion)
{
    public static QuoteView From(GeneralQuote q) => new(q.Id, q.QuoteNumber, q.QuoteVersion, q.ProductCode, q.ProductVersion, q.ProposerPartyId, q.IntermediaryCode,
        q.Status, q.ValidUntil, q.PeriodStart, q.PeriodEnd, q.Premium, q.Currency, q.RatingRequestId, q.RatingVersion,
        q.Risks.Select(r => new RiskView(r.RiskObjectId, r.SumInsured, null, null,
            r.Coverages.Select(c => new CoverageView(c.CoverageCode, c.SumInsured, c.ExcessScheduleCode, null)).ToList())).ToList(),
        q.Referrals.Select(r => new ReferralView(r.Id, r.ReasonCode, r.Status, r.ReferredBy, r.DecidedBy, r.DecisionCode, r.SpecialConditions ?? [], r.DeclineReason)).ToList(),
        q.RowVersion);
}

public sealed record ProposalView(Guid Id, Guid QuoteId, ProposalStatus Status, DateTimeOffset SubmittedAt, JsonElement Declarations)
{
    public static ProposalView From(GeneralProposal p) => new(p.Id, p.QuoteId, p.Status, p.SubmittedAt, JsonDocument.Parse(p.Declarations).RootElement.Clone());
}

public sealed record CancellationView(string ReasonCode, DateOnly EffectiveDate, decimal RefundAmount, string RefundBasis, decimal Fee, DateTimeOffset? CancelledAt,
    DateTimeOffset? ReinstatedAt);

public sealed record PolicyView(Guid Id, string PolicyNumber, string ProductCode, int ProductVersion, int RatingVersion, Guid PolicyholderPartyId, PolicyStatus Status,
    DateOnly PeriodStart, DateOnly PeriodEnd, string Currency, decimal AnnualPremium, Guid BillingAccountRef, string? BillingFrequency, string? CollectionMethod,
    Guid? RenewalOfId, string CoverStartsOn, DateOnly? FirstPaidOn, IReadOnlyList<RiskView> Risks, IReadOnlyList<CancellationView> Cancellations, long RowVersion)
{
    public static PolicyView From(GeneralPolicy p) => new(p.Id, p.PolicyNumber, p.ProductCode, p.ProductVersion, p.RatingVersion, p.PolicyholderPartyId, p.Status,
        p.PeriodStart, p.PeriodEnd, p.Currency, p.AnnualPremium, p.BillingAccountRef, p.BillingFrequency, p.CollectionMethod, p.RenewalOfId, p.CoverStartsOn, p.FirstPaidOn,
        p.Risks.Select(r => new RiskView(r.RiskObjectId, r.SumInsured, r.AttachedFrom, r.DetachedOn,
            r.Coverages.Select(c => new CoverageView(c.CoverageCode, c.SumInsured, c.ExcessScheduleCode, c.Premium)).ToList())).ToList(),
        p.Cancellations.Select(c => new CancellationView(c.ReasonCode, c.EffectiveDate, c.RefundAmount, c.RefundBasis, c.Fee, c.CancelledAt, c.ReinstatedAt)).ToList(),
        p.RowVersion);
}

public sealed record EndorsementView(Guid Id, string EndorsementNumber, Guid PolicyId, string EndorsementType, DateOnly EffectiveDate, EndorsementStatus Status,
    decimal PremiumDelta, decimal? NewAnnualPremium, Guid? RatingRequestId, Guid? DocumentId, IReadOnlyList<EndorsementChange> Changes, long RowVersion,
    Workflow.Contracts.LatestRejection? LatestRejection = null)
{
    public static EndorsementView From(GeneralEndorsement e) => new(e.Id, e.EndorsementNumber, e.PolicyId, e.EndorsementType, e.EffectiveDate, e.Status,
        e.PremiumDelta, e.NewAnnualPremium, e.RatingRequestId, e.DocumentId, e.Changes, e.RowVersion);
}

public sealed record ClaimPartyView(Guid PartyId, string Role, string? InsurerRef);

public sealed record SettlementView(decimal Amount, string Currency, Guid PayeePartyId, string ReserveType, bool Pending, Guid? ApprovalId);

public sealed record ClaimView(Guid Id, string? ClaimNumber, Guid PolicyId, Guid? RiskObjectId, string CoverageCode, DateOnly LossDate, DateTimeOffset NotifiedAt,
    string CauseCode, string PerilCode, string? CatEventCode, ClaimStatus Status, string? DecisionCode, string? DecisionRationale, SettlementView? Settlement,
    IReadOnlyList<ClaimPartyView> Parties, long RowVersion,
    [property: OneOf(ClaimDecisionOutcomes.Approved, ClaimDecisionOutcomes.PartlyApproved, ClaimDecisionOutcomes.Declined)] string? Outcome = null,
    decimal? ClaimedAmount = null, Workflow.Contracts.LatestRejection? LatestRejection = null)
{
    public static ClaimView From(GeneralClaim c) => new(c.Id, c.ClaimNumber, c.PolicyId, c.RiskObjectId, c.CoverageCode, c.LossDate, c.NotifiedAt, c.CauseCode, c.PerilCode,
        c.CatEventCode, c.Status, c.DecisionCode, c.DecisionRationale,
        c.SettlementAmount is { } amount ? new SettlementView(amount, c.SettlementCurrency!, c.SettlementPayeePartyId!.Value, c.SettlementReserveType!, c.SettlementPending,
            c.SettlementApprovalId) : null,
        c.Parties.Select(p => new ClaimPartyView(p.PartyId, p.Role, p.InsurerRef)).ToList(), c.RowVersion, c.DecisionOutcome, c.ClaimedAmount);
}

public sealed record EstimateView(Guid Id, Guid RepairerPartyId, decimal Amount, [property: OneOf("SUBMITTED", "ACCEPTED", "REJECTED")] string Status);

public sealed record AssessmentView(Guid Id, Guid ClaimId, Guid AssessorPartyId, AssessmentStatus Status, decimal? AssessedAmount, decimal? ExcessApplied, Guid? ReportDocumentId,
    DateTimeOffset? AssessedAt, IReadOnlyList<EstimateView> Estimates, long RowVersion)
{
    public static AssessmentView From(LossAssessment a) => new(a.Id, a.ClaimId, a.AssessorPartyId, a.Status, a.AssessedAmount, a.ExcessApplied, a.ReportDocumentId,
        a.AssessedAt, a.Estimates.Select(e => new EstimateView(e.Id, e.RepairerPartyId, e.Amount, e.Status)).ToList(), a.RowVersion);
}

public sealed record SalvageView(Guid Id, string Description, string? DisposalMethod, decimal? Proceeds);

public sealed record RecoveryView(Guid Id, Guid ClaimId, string RecoveryType, Guid? CounterpartyPartyId, decimal ExpectedAmount, decimal RecoveredAmount, RecoveryStatus Status,
    IReadOnlyList<SalvageView> Salvage, long RowVersion)
{
    public static RecoveryView From(RecoveryCase r) => new(r.Id, r.ClaimId, r.RecoveryType, r.CounterpartyPartyId, r.ExpectedAmount, r.RecoveredAmount, r.Status,
        r.Salvage.Select(s => new SalvageView(s.Id, s.Description, s.DisposalMethod, s.Proceeds)).ToList(), r.RowVersion);
}
