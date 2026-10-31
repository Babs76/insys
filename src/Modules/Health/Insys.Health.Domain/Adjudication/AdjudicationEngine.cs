/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Health
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
using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Domain.Expressions;

namespace Insys.Health.Domain.Adjudication;

// The adjudication of a medical claim, in ADR-012's order (proposed, pending business sign-off). Every rule it applies
// is static data; this class only combines them. It is pure: it reads what it is given and returns the line results
// and the accumulator changes, which the caller applies with the claim in one transaction.

public enum NetworkStatus
{
    InNetwork,
    OutOfNetwork,
}

public enum LineOutcome
{
    Pay,
    Pend,
    Reject,
}

/// <summary>A reviewer's answer to a pended line: accept it (the check that pended it is passed) or reject it.</summary>
public enum ReviewDecision
{
    Accept,
    Reject,
}

public static class AdjudicationReasons
{
    public const string NoBenefitCategory = "NO_BENEFIT_CATEGORY";
    public const string NoTariff = "NO_TARIFF";
    public const string NoNetworkRule = "NO_NETWORK_RULE";
    public const string OutOfNetwork = "OUT_OF_NETWORK";
    public const string ModifierNotAllowed = "MODIFIER_NOT_ALLOWED";
    public const string WaitingPeriod = "WAITING_PERIOD";
    public const string Excluded = "EXCLUDED";
    public const string Duplicate = "DUPLICATE";
    public const string RepeatService = "REPEAT_SERVICE";
    public const string OverAuthorised = "OVER_AUTHORISED";
    public const string PreauthRequired = "PREAUTH_REQUIRED";
    public const string MemberClaim = "MEMBER_CLAIM";
    public const string Limit = "LIMIT";
    public const string Reviewer = "REVIEWER";
    public const string NotOnFormulary = "NOT_ON_FORMULARY";
    public const string TierNotCovered = "TIER_NOT_COVERED";
    public const string NoPrescriptionBenefit = "NO_PRESCRIPTION_BENEFIT";
    public const string PrescriptionNotValid = "PRESCRIPTION_NOT_VALID";
    public const string RepeatExceeded = "REPEAT_EXCEEDED";
    public const string QuantityCut = "QUANTITY_CUT";
    public const string DuplicateTherapy = "DUPLICATE_THERAPY";
}

public static class AccumulatorScope
{
    public const string Member = "MEMBER";
    public const string Family = "FAMILY";
}

/// <summary>The plan option the member is on (health.cfg_plans_and_options).</summary>
/// <param name="MemberClaimBasis">How member reimbursement claims are adjudicated (health.cfg_plans_and_options.member_claim_basis): REFERENCE_TARIFF or PEND.</param>
public sealed record PlanTerms(string PlanCode, string OptionCode, string BenefitScheduleCode, string NetworkCode, string? ReferenceTariffSchedule, string BenefitYear,
    string MemberClaimBasis = MemberClaimBases.ReferenceTariff);

public static class MemberClaimBases
{
    public const string ReferenceTariff = "REFERENCE_TARIFF";
    public const string Pend = "PEND";
}

/// <summary>The provider: its type (for the category fallback), whether it is in the plan's network, and the schedules of its contract and the network.</summary>
public sealed record ProviderTerms(string? ProviderId, string? ProviderType, bool InNetwork, string? ContractTariffSchedule, string? NetworkTariffSchedule);

/// <summary>A tariff price (health.cfg_tariff_schedules) and the modifiers it allows; null allows any.</summary>
public sealed record TariffPrice(decimal Price, IReadOnlyList<string>? AllowedModifiers);

/// <summary>A tariff modifier in force (health.cfg_tariff_modifiers).</summary>
public sealed record ModifierRule(string Code, string Type, decimal Value, IReadOnlyList<string>? AppliesTo, int SortOrder);

/// <summary>A benefit category mapping in force (health.cfg_benefit_category_mappings): a code range, or a fallback by provider type.</summary>
public sealed record CategoryMapping(string? CodeSystem, string? CodeFrom, string? CodeTo, string? ProviderType, string Category);

/// <summary>A co-payment or deductible rule in force (health.cfg_co_payment_and_deductible_rules).</summary>
public sealed record CostShareRule(string Kind, string Category, string Type, decimal Value, string NetworkStatus, decimal? Cap, string CapBasis, string? PlanCode);

/// <summary>A benefit schedule row (health.cfg_benefit_schedules): a limit per event, per year or for life.</summary>
public sealed record ScheduleLimit(string Category, decimal Limit, string Frequency);

/// <summary>A limit definition (health.cfg_limit_definitions): annual, lifetime or per event; per member or per family.</summary>
public sealed record LimitDefinition(string Category, string Period, decimal Amount, string Basis, string? PlanCode);

public sealed record ExclusionRule(string Code, IReadOnlyList<string> DiagnosisCodes, IReadOnlyList<string> ProcedureCodes);

/// <summary>A waiting period fixed at enrolment: until <see cref="EndsOn"/> (exclusive), except for the exception categories.</summary>
public sealed record WaitingPeriod(string RuleCode, string Type, DateOnly EndsOn, IReadOnlyList<string> Exceptions, IReadOnlyList<string> DiagnosisCodes,
    IReadOnlyList<string> ProcedureCodes);

/// <summary>A service needing preauthorisation above a threshold (health.cfg_preauthorisation_required_lists): a tariff code or a benefit category.</summary>
public sealed record PreauthRequirement(string Service, decimal Threshold);

/// <summary>An authorised line of a preauthorisation that matches the claim's member, patient, provider and service date.</summary>
public sealed record AuthorisedLine(Guid PreauthorisationId, Guid LineId, string ProcedureCode, decimal Authorised, decimal Reserved, decimal Consumed, string? Category);

/// <summary>An earlier service on another claim for the patient that was not rejected or reversed.</summary>
public sealed record PriorService(Guid ClaimId, string TariffCode, string? Category, DateOnly ServiceDate, string? ProviderId);

/// <summary>A repeat-service interval (health.cfg_repeat_service_intervals): the service is a tariff code or a benefit category.</summary>
public sealed record RepeatRule(string Service, string MinInterval);

/// <summary>A high-cost threshold (health.cfg_high_cost_thresholds): a tariff code or benefit category above which a utilisation review opens.</summary>
public sealed record HighCostRule(string Code, string Service, decimal Amount);

/// <summary>
/// An adjudication edit rule. Its condition reads the line's own facts; a rule with a window (window_days) also reads
/// the person's claim history within that many days before the service date (Phase 5 review, item 5; EDIT-DUP-30):
/// <c>prior_same_code</c>, <c>prior_same_code_provider</c> and <c>prior_same_category</c> (counts of services on other
/// claims not rejected or reversed) and <c>days_since_same_code</c> (absent when there is none).
/// </summary>
public sealed record EditRule(string Code, Expression Condition, string Outcome, string ReasonCode, int? WindowDays = null);

public sealed record AccumulatorKey(string Scope, Guid BeneficiaryPartyId, string Category, DateOnly PeriodStart, DateOnly PeriodEnd);

public sealed record AccumulatorState(decimal? Limit, decimal Used, decimal Reserved, decimal DeductibleMet, decimal CopayTaken)
{
    public decimal? Remaining => Limit is { } limit ? limit - Used - Reserved : null;
}

public sealed record ClaimLineInput(int LineNo, string TariffCode, string? DiagnosisCode, int Quantity, decimal Claimed, IReadOnlyList<string> Modifiers, PharmacyFacts? Pharmacy = null);

/// <summary>What makes a claim line a pharmacy line (addenda/benefit-programmes.md §4.1): its TariffCode is then the product code. Clinical.</summary>
public sealed record PharmacyFacts(int? DaysSupply, DateOnly? PrescribedOn, int? RepeatNumber, int? RepeatsAuthorised);

/// <summary>A medicine in force on the service date (health.ref_medicine_formulary).</summary>
public sealed record FormularyItem(string ProductCode, string ActiveIngredient, string Tier, decimal? ReferencePrice, string? Category, bool PriorAuthRequired, int? MaxQuantity, int? MaxDaysSupply);

/// <summary>What a plan gives a formulary tier (health.cfg_prescription_benefits); a null plan is every plan. <see cref="FormularyTiers.NonFormulary"/> also covers a product not on the list (OQ-149).</summary>
public sealed record PrescriptionBenefit(string? PlanCode, string Tier, bool Covered, string? Category, string? CopayType, decimal? CopayValue, decimal? LimitAmount, int? ValidityDays,
    decimal? RefillTolerancePercent);

public static class FormularyTiers
{
    public const string NonFormulary = "NON_FORMULARY";
}

/// <summary>A medicine paid on another adjudicated claim for the patient, not rejected or reversed. What a limit has used is counted from these, so a reversal gives it back.</summary>
public sealed record PriorMedicine(Guid ClaimId, string ProductCode, string? ActiveIngredient, string? Tier, DateOnly ServiceDate, int Quantity, int? DaysSupply, decimal Paid);

/// <summary>A medicine allowed under a chronic registration, with its monthly quantity. Above it, the excess isn't paid (§4.3).</summary>
public sealed record ChronicEntitlement(string? ProductCode, string? ActiveIngredient, int MonthlyQuantity)
{
    public bool Covers(string productCode, string? ingredient) => ProductCode == productCode || (ActiveIngredient is not null && ActiveIngredient == ingredient);
}

/// <summary>An approved registration in force for the patient on the service date, with its condition's benefit category and diagnosis codes (F084).</summary>
public sealed record ChronicBenefit(string Category, IReadOnlyList<string> DiagnosisCodes, IReadOnlyList<ChronicEntitlement> Entitlements);

/// <summary>A limit on a product or an active ingredient (health.cfg_medication_limits), on top of category limits; the most restrictive wins (F046).</summary>
public sealed record MedicationLimit(string? PlanCode, string? ProductCode, string? ActiveIngredient, int? MaxQuantity, decimal? MaxAmount, string Period)
{
    public bool AppliesTo(string productCode, string? ingredient, string planCode) =>
        (PlanCode is null || PlanCode == planCode) && (ProductCode == productCode || (ActiveIngredient is not null && ActiveIngredient == ingredient));

    /// <summary>What it has counted: medicines of the same product or ingredient, served within the period before the day.</summary>
    public IEnumerable<PriorMedicine> Counted(IEnumerable<PriorMedicine> medicines, DateOnly day) =>
        medicines.Where(m => (ProductCode is not null ? m.ProductCode == ProductCode : m.ActiveIngredient == ActiveIngredient)
                             && m.ServiceDate <= day && IsoDuration.Add(m.ServiceDate, Period) is { } ends && ends > day);
}

/// <summary>A pharmacy line's formulary entry (null when it isn't listed), the benefit for its tier and the benefit category it draws on.</summary>
public sealed record PharmacyTerms(FormularyItem? Item, PrescriptionBenefit? Benefit, string? Category)
{
    public string Tier => Item?.Tier ?? FormularyTiers.NonFormulary;

    public static PharmacyTerms Of(string productCode, AdjudicationContext context)
    {
        var item = context.Formulary.GetValueOrDefault(productCode);
        var tier = item?.Tier ?? FormularyTiers.NonFormulary;
        var benefit = context.PrescriptionBenefits.Where(b => b.Tier == tier && (b.PlanCode is null || b.PlanCode == context.Plan.PlanCode))
            .OrderBy(b => b.PlanCode is null).FirstOrDefault();
        return new PharmacyTerms(item, benefit, item?.Category ?? benefit?.Category);
    }

    /// <summary>What is paid for: at most the maximum quantity and, pro rata, the maximum days' supply. The excess is not paid (F042).</summary>
    public int PayableQuantity(int quantity, int? daysSupply)
    {
        var payable = Item?.MaxQuantity is { } most ? Math.Min(quantity, most) : quantity;
        if (Item?.MaxDaysSupply is { } days && daysSupply is { } supplied && supplied > days)
        {
            payable = Math.Min(payable, quantity * days / supplied);
        }

        return payable;
    }

    /// <summary>
    /// The same ingredient while the last days' supply hasn't run out, less the plan's early refill tolerance (OQ-151, default 10%), counted in whole days.
    /// </summary>
    public bool RefillTooEarly(IEnumerable<PriorMedicine> medicines, DateOnly day)
    {
        var tolerance = Benefit?.RefillTolerancePercent ?? DefaultRefillTolerancePercent;
        return Item?.ActiveIngredient is { } ingredient && medicines.Any(m => m.ActiveIngredient == ingredient && m.DaysSupply is { } days && m.ServiceDate <= day
            && day.DayNumber - m.ServiceDate.DayNumber < (int)Math.Ceiling(days * (100m - tolerance) / 100m));
    }

    public const decimal DefaultRefillTolerancePercent = 10m;
}

public sealed record AdjudicationContext
{
    public required Guid ClaimId { get; init; }

    public required DateOnly ServiceDate { get; init; }

    public required Guid PatientPartyId { get; init; }

    public required Guid PrincipalPartyId { get; init; }

    public required DateOnly MembershipStart { get; init; }

    /// <summary>The membership's contributions are in arrears on the service date and it is not suspended: the claim is held (F006).</summary>
    public bool HeldForArrears { get; init; }

    public bool MemberClaim { get; init; }

    public required PlanTerms Plan { get; init; }

    public required ProviderTerms Provider { get; init; }

    /// <summary>The plan's network restriction rule: NOT_COVERED, CO_PAYMENT or COVERED_AT_TARIFF; null when none is in force.</summary>
    public string? OutOfNetworkTreatment { get; init; }

    public IReadOnlyDictionary<(string Schedule, string TariffCode), TariffPrice> Tariffs { get; init; } = new Dictionary<(string, string), TariffPrice>();

    public IReadOnlyList<ModifierRule> Modifiers { get; init; } = [];

    public IReadOnlyList<CategoryMapping> Mappings { get; init; } = [];

    /// <summary>The code system of each tariff code (health.ref_procedure_and_service_codes).</summary>
    public IReadOnlyDictionary<string, string> CodeSystems { get; init; } = new Dictionary<string, string>();

    public IReadOnlyList<CostShareRule> CostShares { get; init; } = [];

    public IReadOnlyList<ScheduleLimit> ScheduleLimits { get; init; } = [];

    public IReadOnlyList<LimitDefinition> LimitDefinitions { get; init; } = [];

    public IReadOnlyList<ExclusionRule> Exclusions { get; init; } = [];

    public IReadOnlyList<WaitingPeriod> WaitingPeriods { get; init; } = [];

    public IReadOnlyList<PreauthRequirement> PreauthRequired { get; init; } = [];

    public IReadOnlyList<AuthorisedLine> Authorised { get; init; } = [];

    public IReadOnlyList<PriorService> Prior { get; init; } = [];

    public IReadOnlyList<RepeatRule> Repeats { get; init; } = [];

    public IReadOnlyList<HighCostRule> HighCost { get; init; } = [];

    public IReadOnlyList<EditRule> Edits { get; init; } = [];

    public IReadOnlyDictionary<AccumulatorKey, AccumulatorState> Accumulators { get; init; } = new Dictionary<AccumulatorKey, AccumulatorState>();

    public IReadOnlyDictionary<int, ReviewDecision> Reviews { get; init; } = new Dictionary<int, ReviewDecision>();

    public IReadOnlyDictionary<string, FormularyItem> Formulary { get; init; } = new Dictionary<string, FormularyItem>();

    public IReadOnlyList<PrescriptionBenefit> PrescriptionBenefits { get; init; } = [];

    public IReadOnlyList<PriorMedicine> PriorMedicines { get; init; } = [];

    public IReadOnlyList<MedicationLimit> MedicationLimits { get; init; } = [];

    public IReadOnlyList<ChronicBenefit> ChronicBenefits { get; init; } = [];
}

public sealed record LineResult(int LineNo, LineOutcome Outcome, string? Reason, string? Category, NetworkStatus? NetworkStatus, decimal? Tariff, decimal Allowed,
    decimal Deductible, decimal CoPayment, decimal Paid, decimal MemberLiability, Guid? PreauthLineId, string? HighCostRule, int? PaidQuantity = null, string? ActiveIngredient = null,
    string? FormularyTier = null);

/// <summary>What the claim does to an accumulator: its limit (when created), and what is consumed, released from reservation, and met.</summary>
public sealed record AccumulatorChange(AccumulatorKey Key, decimal? Limit, decimal Consume, decimal Release, decimal Deductible, decimal CoPayment);

/// <summary>What a matched preauthorisation line releases and consumes.</summary>
public sealed record AuthorisationUse(Guid PreauthorisationId, Guid LineId, decimal Released, decimal Consumed);

public sealed record AdjudicationResult(IReadOnlyList<LineResult> Lines, IReadOnlyList<AccumulatorChange> Changes, IReadOnlyList<AuthorisationUse> AuthorisationUses)
{
    public bool Pended => Lines.Any(l => l.Outcome == LineOutcome.Pend);

    public decimal Paid => Lines.Sum(l => l.Paid);
}

/// <summary>The benefit periods of a plan: the benefit year (calendar or membership year) and the lifetime.</summary>
public static class BenefitPeriods
{
    public const string Calendar = "CALENDAR";
    public static readonly DateOnly LifetimeEnd = new(9999, 12, 31);

    public static (DateOnly Start, DateOnly End) Year(string benefitYear, DateOnly membershipStart, DateOnly day)
    {
        if (benefitYear != "MEMBERSHIP_YEAR")
        {
            return (new DateOnly(day.Year, 1, 1), new DateOnly(day.Year, 12, 31));
        }

        var years = day.Year - membershipStart.Year;
        var start = membershipStart.AddYears(years);
        if (start > day)
        {
            start = membershipStart.AddYears(years - 1);
        }

        return (start, start.AddYears(1).AddDays(-1));
    }

    public static (DateOnly Start, DateOnly End) Lifetime(DateOnly membershipStart) => (membershipStart, LifetimeEnd);
}

/// <summary>The claim line's benefit category: the most specific mapping of its code, else the provider type's fallback (ADR-012, a).</summary>
public static class BenefitCategories
{
    public static string? Of(string code, string? codeSystem, string? providerType, IReadOnlyList<CategoryMapping> mappings) =>
        mappings.Where(m => m.CodeSystem is not null && m.CodeSystem == codeSystem && m.CodeFrom is not null && m.CodeTo is not null
                            && CodeRanges.Contains($"{m.CodeFrom}-{m.CodeTo}", code))
            .OrderBy(m => CodeRanges.Specificity(m.CodeFrom!, m.CodeTo!)).ThenBy(m => m.Category, StringComparer.Ordinal)
            .Select(m => m.Category).FirstOrDefault()
        ?? mappings.Where(m => m.CodeSystem is null && providerType is not null && m.ProviderType == providerType).Select(m => m.Category).FirstOrDefault();
}

/// <summary>
/// The limits that apply to a category for a patient on a day (ADR-012, f): the plan's benefit schedule (per event, per
/// year or lifetime, per member) and the limit definitions for the plan or all plans (annual or lifetime per member or
/// per family; per event). A family limit is one pool on the principal. Two limits on one accumulator keep the lower.
/// </summary>
public static class Limits
{
    public static (IReadOnlyDictionary<AccumulatorKey, decimal?> Accumulators, decimal? PerEvent) For(string category, AdjudicationContext context, DateOnly day)
    {
        var year = BenefitPeriods.Year(context.Plan.BenefitYear, context.MembershipStart, day);
        var life = BenefitPeriods.Lifetime(context.MembershipStart);
        var keys = new Dictionary<AccumulatorKey, decimal?>();
        decimal? perEvent = null;

        void Add(string scope, Guid beneficiary, (DateOnly Start, DateOnly End) period, decimal? limit)
        {
            var key = new AccumulatorKey(scope, beneficiary, category, period.Start, period.End);
            keys[key] = keys.TryGetValue(key, out var existing) ? Lower(existing, limit) : limit;
        }

        // The member's benefit-year accumulator always exists: deductibles and co-payments are tracked on it.
        Add(AccumulatorScope.Member, context.PatientPartyId, year, null);
        foreach (var limit in context.ScheduleLimits.Where(l => l.Category == category))
        {
            switch (limit.Frequency)
            {
                case "PER_EVENT":
                    perEvent = Lower(perEvent, limit.Limit);
                    break;
                case "LIFETIME":
                    Add(AccumulatorScope.Member, context.PatientPartyId, life, limit.Limit);
                    break;
                default:
                    Add(AccumulatorScope.Member, context.PatientPartyId, year, limit.Limit);
                    break;
            }
        }

        foreach (var definition in context.LimitDefinitions.Where(d => d.Category == category && (d.PlanCode is null || d.PlanCode == context.Plan.PlanCode)))
        {
            if (definition.Period == "PER_EVENT")
            {
                perEvent = Lower(perEvent, definition.Amount);
                continue;
            }

            var family = definition.Basis == "PER_FAMILY";
            Add(family ? AccumulatorScope.Family : AccumulatorScope.Member, family ? context.PrincipalPartyId : context.PatientPartyId,
                definition.Period == "LIFETIME" ? life : year, definition.Amount);
        }

        return (keys, perEvent);
    }

    private static decimal? Lower(decimal? a, decimal? b) => a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);
}

/// <summary>The adjudication of a claim's lines in ADR-012's order (proposed, pending business sign-off).</summary>
public static class AdjudicationEngine
{
    /// <summary>The line's facts with the person's history within the window (days before the service date, other claims only).</summary>
    private static Dictionary<string, object?> WithHistory(Dictionary<string, object?> facts, AdjudicationContext context, string tariffCode, string? category, int window)
    {
        var prior = context.Prior.Where(p => p.ClaimId != context.ClaimId && p.ServiceDate <= context.ServiceDate && p.ServiceDate >= context.ServiceDate.AddDays(-window))
            .ToList();
        var sameCode = prior.Where(p => p.TariffCode == tariffCode).ToList();
        var scope = new Dictionary<string, object?>(facts, StringComparer.Ordinal)
        {
            ["prior_same_code"] = (decimal)sameCode.Count,
            ["prior_same_code_provider"] = (decimal)sameCode.Count(p => p.ProviderId == context.Provider.ProviderId),
            ["prior_same_category"] = (decimal)prior.Count(p => category is not null && p.Category == category),
        };
        if (sameCode.Count > 0)
        {
            scope["days_since_same_code"] = (decimal)(context.ServiceDate.DayNumber - sameCode.Max(p => p.ServiceDate).DayNumber);
        }

        return scope;
    }

    /// <summary>The registration a line draws on: its diagnosis is on the condition's list, or its medicine is entitled.</summary>
    private static ChronicBenefit? ChronicFor(ClaimLineInput line, FormularyItem? item, AdjudicationContext context) =>
        context.ChronicBenefits.FirstOrDefault(c => (line.DiagnosisCode is not null && CodeRanges.Contains(c.DiagnosisCodes, line.DiagnosisCode))
            || (line.Pharmacy is not null && c.Entitlements.Any(e => e.Covers(line.TariffCode, item?.ActiveIngredient))));

    /// <summary>The benefit category a line draws on, whichever accumulators it needs (ADR-012, a; a registered condition first).</summary>
    public static string? CategoryOf(ClaimLineInput line, AdjudicationContext context)
    {
        var rx = line.Pharmacy is null ? null : PharmacyTerms.Of(line.TariffCode, context);
        return ChronicFor(line, rx?.Item, context)?.Category ?? (rx is not null ? rx.Category
            : BenefitCategories.Of(line.TariffCode, context.CodeSystems.GetValueOrDefault(line.TariffCode), context.Provider.ProviderType, context.Mappings));
    }

    /// <summary>The pend reason of a claim held while the membership's contributions are in arrears (F006).</summary>
    public const string HeldForArrearsReason = "CONTRIBUTIONS_UNPAID";

    public static AdjudicationResult Adjudicate(AdjudicationContext context, IReadOnlyList<ClaimLineInput> lines)
    {
        // F006: in grace the claim is held, not declined: every line pends until the contributions are paid or grace ends.
        if (context.HeldForArrears)
        {
            return new AdjudicationResult(lines.OrderBy(l => l.LineNo)
                .Select(l => new LineResult(l.LineNo, LineOutcome.Pend, HeldForArrearsReason, null, null, null, 0, 0, 0, 0, l.Claimed, null, null)).ToList(), [], []);
        }

        var state = context.Accumulators.ToDictionary(a => a.Key, a => a.Value);
        var changes = new Dictionary<AccumulatorKey, AccumulatorChange>();
        var uses = new List<AuthorisationUse>();
        var authorised = context.Authorised.ToDictionary(a => a.LineId, a => a);
        var perEventUsed = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var copayInClaim = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var medicines = context.PriorMedicines.Where(m => m.ClaimId != context.ClaimId).ToList();
        var results = new List<LineResult>();
        foreach (var line in lines.OrderBy(l => l.LineNo))
        {
            var result = Line(context, line, state, changes, uses, authorised, perEventUsed, copayInClaim, medicines);
            results.Add(result);
        }

        return new AdjudicationResult(results, changes.Values.ToList(), uses);
    }

    private static LineResult Line(AdjudicationContext context, ClaimLineInput line, Dictionary<AccumulatorKey, AccumulatorState> state,
        Dictionary<AccumulatorKey, AccumulatorChange> changes, List<AuthorisationUse> uses, Dictionary<Guid, AuthorisedLine> authorised,
        Dictionary<string, decimal> perEventUsed, Dictionary<string, decimal> copayInClaim, List<PriorMedicine> medicines)
    {
        context.Reviews.TryGetValue(line.LineNo, out var review);
        var accepted = context.Reviews.ContainsKey(line.LineNo) && review == ReviewDecision.Accept;
        LineResult Stop(LineOutcome outcome, string reason, string? category = null, NetworkStatus? network = null, decimal? tariff = null, decimal allowed = 0) =>
            new(line.LineNo, outcome, reason, category, network, tariff, allowed, 0, 0, 0, line.Claimed, null, null);

        if (context.Reviews.ContainsKey(line.LineNo) && review == ReviewDecision.Reject)
        {
            return Stop(LineOutcome.Reject, AdjudicationReasons.Reviewer);
        }

        // A member reimbursement claim (Phase 5 review, item 5): on the plan's REFERENCE_TARIFF basis it is priced at the
        // plan's reference tariff and covered at tariff, like any line (exceptions pend on their own reasons); on PEND a person adjudicates it.
        var memberPriced = context.MemberClaim && context.Plan.MemberClaimBasis == MemberClaimBases.ReferenceTariff;
        if (context.MemberClaim && !memberPriced && !accepted)
        {
            return Stop(LineOutcome.Pend, AdjudicationReasons.MemberClaim);
        }

        // A pharmacy line: coverage from the formulary and the plan's benefit for its tier (F041, OQ-149: not listed and no rule for it is rejected).
        var rx = line.Pharmacy is null ? null : PharmacyTerms.Of(line.TariffCode, context);
        if (rx is { Benefit: null or { Covered: false } } && (rx.Tier == FormularyTiers.NonFormulary || rx.Benefit is not null))
        {
            return Stop(LineOutcome.Reject, rx.Tier == FormularyTiers.NonFormulary ? AdjudicationReasons.NotOnFormulary : AdjudicationReasons.TierNotCovered);
        }

        if (rx is { Benefit: null })
        {
            return Stop(LineOutcome.Pend, AdjudicationReasons.NoPrescriptionBenefit);
        }

        // (a) Benefit category.
        var category = CategoryOf(line, context);
        if (category is null)
        {
            return Stop(LineOutcome.Pend, AdjudicationReasons.NoBenefitCategory);
        }

        // Network status, and (b) the tariff: contract, then network, then the plan's reference schedule; out of network, the reference schedule.
        var network = context.Provider.InNetwork && !memberPriced ? NetworkStatus.InNetwork : NetworkStatus.OutOfNetwork;
        if (network == NetworkStatus.OutOfNetwork && !memberPriced)
        {
            switch (context.OutOfNetworkTreatment)
            {
                case null:
                    return Stop(LineOutcome.Pend, AdjudicationReasons.NoNetworkRule, category, network);
                case "NOT_COVERED":
                    return Stop(LineOutcome.Reject, AdjudicationReasons.OutOfNetwork, category, network);
            }
        }

        var payableQuantity = rx?.PayableQuantity(line.Quantity, line.Pharmacy!.DaysSupply);
        var medicationLimits = rx is null ? [] : context.MedicationLimits.Where(l => l.AppliesTo(line.TariffCode, rx.Item?.ActiveIngredient, context.Plan.PlanCode)).ToList();
        if (rx is not null && ChronicFor(line, rx.Item, context)?.Entitlements.FirstOrDefault(e => e.Covers(line.TariffCode, rx.Item?.ActiveIngredient)) is { } entitlement)
        {
            var usedThisMonth = medicines.Where(m => entitlement.Covers(m.ProductCode, m.ActiveIngredient) && m.ServiceDate.Year == context.ServiceDate.Year && m.ServiceDate.Month == context.ServiceDate.Month)
                .Sum(m => m.Quantity);
            payableQuantity = Math.Min(payableQuantity!.Value, Math.Max(0, entitlement.MonthlyQuantity - usedThisMonth));
        }

        foreach (var limit in medicationLimits.Where(l => l.MaxQuantity is not null))
        {
            payableQuantity = Math.Min(payableQuantity!.Value, Math.Max(0, limit.MaxQuantity!.Value - limit.Counted(medicines, context.ServiceDate).Sum(m => m.Quantity)));
        }

        var schedules = network == NetworkStatus.InNetwork
            ? new[] { context.Provider.ContractTariffSchedule, context.Provider.NetworkTariffSchedule, context.Plan.ReferenceTariffSchedule }
            : [context.Plan.ReferenceTariffSchedule];
        var price = rx is not null ? null : schedules.Where(s => s is not null).Select(s => context.Tariffs.TryGetValue((s!, line.TariffCode), out var p) ? p : null).FirstOrDefault(p => p is not null);
        if (price is null && rx is null)
        {
            return Stop(LineOutcome.Pend, AdjudicationReasons.NoTariff, category, network);
        }

        // (c) Modifiers.
        var unit = price?.Price ?? 0;
        var applied = new List<ModifierRule>();
        foreach (var code in line.Modifiers.Distinct(StringComparer.Ordinal))
        {
            var rule = context.Modifiers.FirstOrDefault(m => m.Code == code);
            if (rule is null || (rule.AppliesTo is { Count: > 0 } && !CodeRanges.Contains(rule.AppliesTo, line.TariffCode))
                || (price?.AllowedModifiers is not null && !price.AllowedModifiers.Contains(code)))
            {
                return Stop(LineOutcome.Pend, AdjudicationReasons.ModifierNotAllowed, category, network);
            }

            applied.Add(rule);
        }

        foreach (var rule in applied.OrderBy(m => m.SortOrder).ThenBy(m => m.Code, StringComparer.Ordinal))
        {
            unit = rule.Type switch
            {
                "PERCENT_UPLIFT" => unit * (1 + rule.Value / 100m),
                "FIXED_ADD" => unit + rule.Value,
                _ => unit * rule.Value,
            };
        }

        var tariff = Round(unit * line.Quantity);

        // (d) Allowed amount: the lesser of claimed and tariff; out of network at the reference tariff. A medicine is priced at the
        // claim for what is payable, capped at the reference price if it has one: paying above it falls to the member (OQ-152).
        var allowed = Math.Min(line.Claimed, tariff);
        if (rx is not null)
        {
            var claimedForPayable = Round(line.Claimed * payableQuantity!.Value / line.Quantity);
            tariff = rx.Item?.ReferencePrice is { } reference ? Round(reference * payableQuantity.Value) : claimedForPayable;
            allowed = Math.Min(claimedForPayable, tariff);
        }

        // (g) Waiting periods fixed at enrolment, and exclusions with codes.
        var waiting = context.WaitingPeriods.FirstOrDefault(w => context.ServiceDate < w.EndsOn && !w.Exceptions.Contains(category)
            && (w.Type == "GENERAL" || CodeRanges.Contains(w.DiagnosisCodes, line.DiagnosisCode) || CodeRanges.Contains(w.ProcedureCodes, line.TariffCode)));
        if (waiting is not null)
        {
            return Stop(LineOutcome.Reject, AdjudicationReasons.WaitingPeriod, category, network, tariff, allowed);
        }

        if (context.Exclusions.Any(e => CodeRanges.Contains(e.DiagnosisCodes, line.DiagnosisCode) || CodeRanges.Contains(e.ProcedureCodes, line.TariffCode)))
        {
            return Stop(LineOutcome.Reject, AdjudicationReasons.Excluded, category, network, tariff, allowed);
        }

        // The prescription (F040): past its validity, or a repeat beyond what was prescribed, pends for a person.
        if (line.Pharmacy is { } script && !accepted)
        {
            if (script.PrescribedOn is { } prescribed
                && (context.ServiceDate < prescribed || (rx!.Benefit!.ValidityDays is { } validity && context.ServiceDate > prescribed.AddDays(validity))))
            {
                return Stop(LineOutcome.Pend, AdjudicationReasons.PrescriptionNotValid, category, network, tariff, allowed);
            }

            if (script.RepeatNumber is { } repeat && repeat > (script.RepeatsAuthorised ?? 0))
            {
                return Stop(LineOutcome.Pend, AdjudicationReasons.RepeatExceeded, category, network, tariff, allowed);
            }
        }

        // The same ingredient before the last supply has run out pends, it isn't rejected (F047).
        if (rx is not null && !accepted && rx.RefillTooEarly(medicines, context.ServiceDate))
        {
            return Stop(LineOutcome.Pend, AdjudicationReasons.DuplicateTherapy, category, network, tariff, allowed);
        }

        // Duplicates (§11.1 H) and repeat services pend for a person.
        if (!accepted && context.Prior.Any(p => p.TariffCode == line.TariffCode && p.ServiceDate == context.ServiceDate && p.ProviderId == context.Provider.ProviderId
                                                && p.ClaimId != context.ClaimId))
        {
            return Stop(LineOutcome.Pend, AdjudicationReasons.Duplicate, category, network, tariff, allowed);
        }

        if (!accepted && context.Repeats.Where(r => r.Service == line.TariffCode || r.Service == category)
                .Any(r => context.Prior.Any(p => p.ClaimId != context.ClaimId && (p.TariffCode == r.Service || p.Category == r.Service)
                                                 && p.ServiceDate <= context.ServiceDate
                                                 && (IsoDuration.Add(p.ServiceDate, r.MinInterval) is not { } next || context.ServiceDate < next))))
        {
            return Stop(LineOutcome.Pend, AdjudicationReasons.RepeatService, category, network, tariff, allowed);
        }

        // Edit rules on the line's own facts, and on the claim history within a rule's window.
        var facts = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["claimed"] = line.Claimed, ["allowed"] = allowed, ["tariff"] = tariff, ["quantity"] = (decimal)line.Quantity, ["tariff_code"] = line.TariffCode,
            ["benefit_category"] = category, ["diagnosis_code"] = line.DiagnosisCode, ["network_status"] = network == NetworkStatus.InNetwork ? "IN_NETWORK" : "OUT_OF_NETWORK",
        };
        foreach (var edit in context.Edits)
        {
            var scope = edit.WindowDays is { } window ? WithHistory(facts, context, line.TariffCode, category, window) : facts;
            if (!edit.Condition.Variables.All(scope.ContainsKey))
            {
                continue;
            }

            var fired = edit.Condition.Evaluate(scope);
            if (fired.IsSuccess && fired.Value && edit.Outcome != "PAY" && !accepted)
            {
                return Stop(edit.Outcome == "REJECT" ? LineOutcome.Reject : LineOutcome.Pend, edit.ReasonCode, category, network, tariff, allowed);
            }
        }

        // (h) Preauthorisation: the authorised line for this code; above what is left of it, the line pends.
        var match = authorised.Values.Where(a => a.ProcedureCode == line.TariffCode).OrderByDescending(a => a.Authorised - a.Consumed).FirstOrDefault();
        if (match is not null && allowed > match.Authorised - match.Consumed && !accepted)
        {
            return Stop(LineOutcome.Pend, AdjudicationReasons.OverAuthorised, category, network, tariff, allowed);
        }

        if (match is null && !accepted
            && (rx?.Item?.PriorAuthRequired == true || context.PreauthRequired.Any(r => (r.Service == line.TariffCode || r.Service == category) && allowed > r.Threshold)))
        {
            return Stop(LineOutcome.Pend, AdjudicationReasons.PreauthRequired, category, network, tariff, allowed);
        }

        var (keys, perEvent) = Limits.For(category, context, context.ServiceDate);
        var memberYear = keys.Keys.First(k => k.Scope == AccumulatorScope.Member && k.PeriodEnd != BenefitPeriods.LifetimeEnd);
        var networkCode = network == NetworkStatus.InNetwork ? "IN_NETWORK" : "OUT_OF_NETWORK";

        // (e) Deductible, then co-payment (with its cap), then limits.
        var year = Current(state, memberYear, keys[memberYear]);
        var deductibleRule = Rule(context, "DEDUCTIBLE", category, networkCode);
        var deductible = deductibleRule is null ? 0 : Math.Min(allowed, Math.Max(0, deductibleRule.Value - year.DeductibleMet));
        var copayRule = rx?.Benefit is { CopayType: { } copayType, CopayValue: { } copayValue }
            ? new CostShareRule("CO_PAYMENT", category, copayType, copayValue, networkCode, null, "NONE", context.Plan.PlanCode) // the tier's co-payment (F043)
            : Rule(context, "CO_PAYMENT", category, memberPriced || context.OutOfNetworkTreatment == "COVERED_AT_TARIFF" ? "IN_NETWORK" : networkCode);
        var copayBase = allowed - deductible;
        var copay = 0m;
        if (copayRule is not null)
        {
            copay = copayRule.Type == "PERCENT" ? Round(copayBase * copayRule.Value / 100m) : Math.Min(copayRule.Value, copayBase);
            if (copayRule.Cap is { } cap)
            {
                var taken = copayRule.CapBasis switch
                {
                    "PER_CLAIM" => copayInClaim.GetValueOrDefault(category),
                    "PER_YEAR" => year.CopayTaken,
                    _ => 0m,
                };
                copay = Math.Min(copay, Math.Max(0, cap - taken));
            }
        }

        var due = allowed - deductible - copay;

        // The matched authorisation's reservation for this line is released before the limits are read, so it is not counted twice.
        var release = match is null ? 0 : Math.Min(match.Reserved, allowed);
        var paid = due;
        foreach (var (key, limit) in keys)
        {
            var current = Current(state, key, limit);
            if (current.Limit is { } l)
            {
                paid = Math.Min(paid, Math.Max(0, l - current.Used - (current.Reserved - release)));
            }
        }

        if (perEvent is { } eventCap)
        {
            paid = Math.Min(paid, Math.Max(0, eventCap - perEventUsed.GetValueOrDefault(category)));
        }

        // The tier's limit for the benefit year, counted from what its medicines were paid.
        if (rx?.Benefit?.LimitAmount is { } tierLimit)
        {
            var benefitYear = BenefitPeriods.Year(context.Plan.BenefitYear, context.MembershipStart, context.ServiceDate);
            var tierUsed = medicines.Where(m => m.Tier == rx.Tier && m.ServiceDate >= benefitYear.Start && m.ServiceDate <= benefitYear.End).Sum(m => m.Paid);
            paid = Math.Min(paid, Math.Max(0, tierLimit - tierUsed));
        }

        foreach (var limit in medicationLimits.Where(l => l.MaxAmount is not null))
        {
            paid = Math.Min(paid, Math.Max(0, limit.MaxAmount!.Value - limit.Counted(medicines, context.ServiceDate).Sum(m => m.Paid)));
        }

        paid = Math.Max(0, paid);
        foreach (var (key, limit) in keys)
        {
            var current = Current(state, key, limit);
            var isYear = key == memberYear;
            state[key] = current with
            {
                Used = current.Used + paid, Reserved = Math.Max(0, current.Reserved - release), DeductibleMet = current.DeductibleMet + (isYear ? deductible : 0),
                CopayTaken = current.CopayTaken + (isYear ? copay : 0),
            };
            changes[key] = changes.TryGetValue(key, out var change)
                ? change with
                {
                    Consume = change.Consume + paid, Release = change.Release + release, Deductible = change.Deductible + (isYear ? deductible : 0),
                    CoPayment = change.CoPayment + (isYear ? copay : 0),
                }
                : new AccumulatorChange(key, limit, paid, release, isYear ? deductible : 0, isYear ? copay : 0);
        }

        perEventUsed[category] = perEventUsed.GetValueOrDefault(category) + paid;
        copayInClaim[category] = copayInClaim.GetValueOrDefault(category) + copay;
        if (match is not null)
        {
            authorised[match.LineId] = match with { Reserved = match.Reserved - release, Consumed = match.Consumed + paid };
            uses.Add(new AuthorisationUse(match.PreauthorisationId, match.LineId, release, paid));
        }

        if (rx is not null)
        {
            medicines.Add(new PriorMedicine(context.ClaimId, line.TariffCode, rx.Item?.ActiveIngredient, rx.Tier, context.ServiceDate, payableQuantity!.Value, line.Pharmacy!.DaysSupply, paid));
        }

        var highCost = context.HighCost.FirstOrDefault(h => (h.Service == line.TariffCode || h.Service == category) && allowed > h.Amount)?.Code;
        var why = paid < due ? AdjudicationReasons.Limit : payableQuantity < line.Quantity ? AdjudicationReasons.QuantityCut : null;
        return new LineResult(line.LineNo, LineOutcome.Pay, why, category, network, tariff, allowed, deductible, copay, paid,
            line.Claimed - paid, match?.LineId, highCost, payableQuantity, rx?.Item?.ActiveIngredient, rx?.Tier);
    }

    private static CostShareRule? Rule(AdjudicationContext context, string kind, string category, string networkStatus) =>
        context.CostShares.Where(r => r.Kind == kind && r.Category == category && r.NetworkStatus == networkStatus && (r.PlanCode is null || r.PlanCode == context.Plan.PlanCode))
            .OrderBy(r => r.PlanCode is null).FirstOrDefault();

    private static AccumulatorState Current(Dictionary<AccumulatorKey, AccumulatorState> state, AccumulatorKey key, decimal? limit)
    {
        if (!state.TryGetValue(key, out var current))
        {
            current = new AccumulatorState(limit, 0, 0, 0, 0);
            state[key] = current;
        }

        return current;
    }

    internal static decimal Round(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);
}

/// <summary>ISO 8601 durations as the static data writes them (P6M, P1Y, P30D).</summary>
public static class IsoDuration
{
    public static DateOnly? Add(DateOnly day, string duration)
    {
        var text = duration.Trim().ToUpperInvariant();
        if (text.Length < 3 || text[0] != 'P')
        {
            return null;
        }

        var result = day;
        var number = 0;
        var digits = false;
        foreach (var c in text[1..])
        {
            if (char.IsAsciiDigit(c))
            {
                number = checked(number * 10 + (c - '0'));
                digits = true;
                continue;
            }

            if (!digits)
            {
                return null;
            }

            switch (c)
            {
                case 'Y':
                    result = result.AddYears(number);
                    break;
                case 'M':
                    result = result.AddMonths(number);
                    break;
                case 'W':
                    result = result.AddDays(number * 7);
                    break;
                case 'D':
                    result = result.AddDays(number);
                    break;
                default:
                    return null;
            }

            number = 0;
            digits = false;
        }

        return digits ? null : result;
    }

    public static string Format(int days) => "P" + days.ToString(CultureInfo.InvariantCulture) + "D";
}
