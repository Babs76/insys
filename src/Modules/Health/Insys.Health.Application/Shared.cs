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
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Domain;
using Insys.Health.Domain.Adjudication;
using Insys.Health.Domain.Benefits;
using Insys.Health.Domain.Care;
using Insys.Health.Domain.Members;
using Insys.Health.Domain.Payments;

namespace Insys.Health.Application;

public interface IHealthRepository
{
    /// <summary>The memberships' numbers, products and nodes, for scoping the claims a payment run pays (F103).</summary>
    Task<IReadOnlyDictionary<Guid, MembershipScope>> MembershipScopes(IReadOnlyCollection<Guid> membershipIds, CancellationToken cancellationToken);

    /// <summary>
    /// Memberships with no product take their plan option's, once a version in force names one (addenda/health-plan-products.md §4).
    /// Returns how many were given one.
    /// </summary>
    Task<int> AssignPlanProducts(DateOnly day, Func<string, CancellationToken, Task<int?>> versionOnSale, DateTimeOffset now, CancellationToken cancellationToken);

    Task<HealthScheme?> Scheme(Guid id, CancellationToken cancellationToken);

    Task<HealthMembership?> Membership(Guid id, CancellationToken cancellationToken);

    /// <summary>The membership's currency (BR-068), for records already read within the caller's scope.</summary>
    Task<string?> MembershipCurrency(Guid id, CancellationToken cancellationToken);

    Task<HealthMembership?> MembershipByNumber(string memberNumber, CancellationToken cancellationToken);

    /// <summary>Locks the membership row, then its accumulators in id order, for the current transaction (SELECT … FOR UPDATE).</summary>
    Task LockBenefits(Guid membershipId, CancellationToken cancellationToken);

    /// <summary>The accumulators with these keys that exist, tracked for change.</summary>
    Task<IReadOnlyList<BenefitAccumulator>> Accumulators(Guid membershipId, IReadOnlyCollection<AccumulatorKey> keys, CancellationToken cancellationToken);

    /// <summary>The membership's accumulators for a category whose period includes the day.</summary>
    Task<IReadOnlyList<BenefitAccumulator>> AccumulatorsCovering(Guid membershipId, string category, DateOnly day, CancellationToken cancellationToken);

    Task<IReadOnlyList<BenefitAccumulator>> AccumulatorsOf(Guid membershipId, CancellationToken cancellationToken);

    Task<Preauthorisation?> Preauthorisation(Guid id, CancellationToken cancellationToken);

    /// <summary>Approved authorisations for the member, patient and provider, valid on the service date (ADR-012, h).</summary>
    Task<IReadOnlyList<Preauthorisation>> MatchingPreauthorisations(Guid membershipId, Guid patientPartyId, string providerId, DateOnly serviceDate,
        CancellationToken cancellationToken);

    Task<MedicalClaim?> Claim(Guid id, CancellationToken cancellationToken);

    /// <summary>The patient's services on other claims from a date, not rejected or reversed, for duplicates and repeat services.</summary>
    Task<IReadOnlyList<PriorService>> PriorServices(Guid patientPartyId, DateOnly from, Guid exceptClaimId, CancellationToken cancellationToken);

    Task<IReadOnlyList<MedicalClaim>> ClaimsForRun(DateTimeOffset cutOff, string currency, CancellationToken cancellationToken);

    Task<IReadOnlyList<MedicalClaim>> ClaimsInRun(Guid runId, CancellationToken cancellationToken);

    Task<UtilisationCase?> UtilisationCase(Guid id, CancellationToken cancellationToken);

    Task<HealthFraudCase?> FraudCase(Guid id, CancellationToken cancellationToken);

    Task<ProviderPaymentRun?> Run(Guid id, CancellationToken cancellationToken);

    Task<ProviderPaymentRun?> RunForRemittance(Guid remittanceId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Guid>> PreauthorisationsToExpire(DateOnly day, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<Guid>> MembershipsToActivate(DateOnly day, int limit, CancellationToken cancellationToken);

    /// <summary>Active or suspended memberships billed on the account (Billing's ArrearsDetected names the account).</summary>
    Task<IReadOnlyList<Guid>> MembershipsOnAccount(Guid billingAccountRef, CancellationToken cancellationToken);

    /// <summary>Active memberships with contributions still unpaid on their suspension day (F006).</summary>
    Task<IReadOnlyList<Guid>> MembershipsToSuspendForArrears(DateOnly day, int limit, CancellationToken cancellationToken);

    /// <summary>Memberships suspended for arrears whose termination notice has run out, not yet raised (F006).</summary>
    Task<IReadOnlyList<Guid>> MembershipsTerminationDue(DateOnly day, int limit, CancellationToken cancellationToken);

    /// <summary>Claims held for arrears (F006): those to release (the arrears were paid) and those to decline (suspended for them).</summary>
    Task<(IReadOnlyList<Guid> Release, IReadOnlyList<Guid> Decline)> HeldClaims(int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<Guid>> SchemesToActivate(DateOnly day, int limit, CancellationToken cancellationToken);

    /// <summary>Active schemes renewing within a year whose renewal notice has not been sent for this year (Wave 4 §4.5).</summary>
    Task<IReadOnlyList<Guid>> SchemesToNotice(DateOnly day, int limit, CancellationToken cancellationToken);

    void Add(HealthScheme scheme);

    void Add(HealthMembership membership);

    void Add(BenefitAccumulator accumulator);

    void Add(EligibilityDecision decision);

    void Add(Preauthorisation preauthorisation);

    void Add(MedicalClaim claim);

    void Add(UtilisationCase utilisationCase);

    // Wave 5a batch 5a.1: provider measures, reviews and statements (F028 to F030).
    Task<IReadOnlyList<string>> ProvidersToMeasure(string monthKey, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProviderClaimFact>> ProviderClaimFacts(string providerId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);

    Task<IReadOnlyList<Domain.Providers.ProviderMeasureResult>> ProviderMeasures(string providerId, string? monthKey, Guid? afterId, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<Domain.Providers.ProviderQualityReview>> ProviderQualityReviews(string providerId, Guid? afterId, int limit, CancellationToken cancellationToken);

    Task<bool> ProviderStatementExists(string providerId, string statementRef, CancellationToken cancellationToken);

    /// <summary>The provider's paid claims with a service date in the period, and any paid claims of its that the references name.</summary>
    Task<IReadOnlyList<(string ClaimNumber, decimal Paid, bool InPeriod)>> ProviderPaidClaims(string providerId, DateOnly from, DateOnly to, IReadOnlyCollection<string> references,
        CancellationToken cancellationToken);

    /// <summary>Those of the provider's claims the references name that were paid and then reversed.</summary>
    Task<IReadOnlyList<(string ClaimNumber, decimal Paid, DateTimeOffset ReversedAt)>> ProviderReversedClaims(string providerId, IReadOnlyCollection<string> references,
        CancellationToken cancellationToken);

    Task<Domain.Providers.ProviderStatement?> ProviderStatement(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProviderStatementSummary>> ProviderStatements(string? providerId, string? status, Guid? afterId, int limit, CancellationToken cancellationToken);

    void Add(Domain.Providers.ProviderMeasureResult result);

    void Add(Domain.Providers.ProviderQualityReview review);

    void Add(Domain.Providers.ProviderStatement statement);

    void Add(HealthFraudCase fraudCase);

    void Add(ProviderPaymentRun run);
}

/// <summary>A plan option in force and the version of its row.</summary>
/// <param name="ProductCode">The Product Factory product the plan is sold as (addenda/health-plan-products.md); null for a plan that names none yet.</param>
public sealed record PlanDefinition(PlanTerms Terms, int Version, string? ProductCode = null);

/// <summary>A scheme member category: the plan it maps to and its eligibility rule (health.cfg_scheme_member_categories).</summary>
public sealed record SchemeCategory(string CategoryCode, string Plan, string EligibilityRule);

/// <summary>A waiting period rule in force (health.cfg_health_waiting_period_rules).</summary>
public sealed record WaitingPeriodRule(string RuleCode, string Type, string Duration, IReadOnlyList<string> Exceptions, IReadOnlyList<string> DiagnosisCodes,
    IReadOnlyList<string> ProcedureCodes);

/// <summary>A provider in the register (health.mst_provider_register).</summary>
public sealed record ProviderInfo(string ProviderId, string? ProviderType, string Status, Guid? PartyId);

/// <summary>A fraud indicator (health.cfg_health_fraud_indicator_definitions): its logic is a condition over the claim's facts.</summary>
public sealed record FraudIndicator(string Code, string Logic, int Score, int Threshold);

/// <summary>The plan's benefit rules in force: everything the adjudication reads that does not depend on the claim's codes.</summary>
public sealed record BenefitRules(IReadOnlyList<ScheduleLimit> ScheduleLimits, IReadOnlyList<LimitDefinition> LimitDefinitions, IReadOnlyList<CostShareRule> CostShares,
    IReadOnlyList<CategoryMapping> Mappings, IReadOnlyList<ModifierRule> Modifiers, IReadOnlyList<PreauthRequirement> PreauthRequired, IReadOnlyList<RepeatRule> Repeats,
    IReadOnlyList<HighCostRule> HighCost, IReadOnlyList<(string Code, string Condition, string Outcome, string ReasonCode, int? WindowDays)> Edits, string? OutOfNetworkTreatment);

/// <summary>The Health module's static data in force.</summary>
public sealed record ContributionRule(decimal Rate, decimal EmployerSharePct);

public interface IHealthCodes
{
    /// <summary>The provider measures in force (health.cfg_provider_measures, F028).</summary>
    Task<IReadOnlyList<Domain.Providers.ProviderMeasure>> ProviderMeasures(DateOnly day, CancellationToken cancellationToken);

    Task<string?> ProviderType(string providerId, DateOnly day, CancellationToken cancellationToken);

    Task<string?> MemberNumberPrefix(DateOnly day, CancellationToken cancellationToken);

    Task<PlanDefinition?> Plan(string planCode, string optionCode, DateOnly day, CancellationToken cancellationToken);

    Task<SchemeCategory?> SchemeCategory(string categoryCode, DateOnly day, CancellationToken cancellationToken);

    /// <summary>The contribution grace for the scheme (its own rule, else the rule naming no scheme), in force on the day (F006); null when none.</summary>
    Task<Domain.Members.ContributionGrace?> ContributionGrace(string? schemeNumber, DateOnly day, CancellationToken cancellationToken);

    /// <summary>Whether the service type is one the tenant offers, in force on the day (health.cfg_service_types, V0094).</summary>
    Task<bool> ServiceTypeInForce(string serviceType, DateOnly day, CancellationToken cancellationToken);

    /// <summary>The monthly contribution per member for a scheme's category, and the employer's share (health.cfg_contribution_rules).</summary>
    Task<ContributionRule?> Contribution(string schemeNumber, string categoryCode, DateOnly day, CancellationToken cancellationToken);

    /// <summary>The sponsor's party (health.mst_sponsor_register.party), when it holds a party id.</summary>
    Task<Guid?> SponsorParty(string sponsorCode, DateOnly day, CancellationToken cancellationToken);

    /// <summary>A scheme's renewal rule (health.cfg_scheme_renewal_rules): its notice days and re-rating basis; null when none is in force.</summary>
    Task<(int NoticeDays, string ReRatingBasis)?> SchemeRenewalRule(string schemeNumber, DateOnly day, CancellationToken cancellationToken);

    /// <summary>The remittance advice template: the format in force first by sort order, then code (health.cfg_remittance_advice_formats).</summary>
    Task<string?> RemittanceTemplate(DateOnly day, CancellationToken cancellationToken);

    /// <summary>The tenant's eligibility response code for an internal outcome (health.ref_eligibility_response_codes.outcome).</summary>
    Task<string?> EligibilityResponseCode(string outcome, DateOnly day, CancellationToken cancellationToken);

    Task<bool> SponsorExists(string sponsorCode, DateOnly day, CancellationToken cancellationToken);

    Task<IReadOnlyList<WaitingPeriodRule>> WaitingPeriodRules(DateOnly day, CancellationToken cancellationToken);

    Task<DependantRule?> DependantType(string typeCode, DateOnly day, CancellationToken cancellationToken);

    Task<int?> ValidityDays(string serviceType, DateOnly day, CancellationToken cancellationToken);

    Task<ProviderInfo?> Provider(string providerId, DateOnly day, CancellationToken cancellationToken);

    Task<BenefitRules> Rules(PlanTerms plan, DateOnly day, CancellationToken cancellationToken);

    /// <summary>Whether the provider is in the plan's network (listed, or contracted with it), and the schedules of its contract and the network.</summary>
    Task<ProviderTerms> Network(PlanTerms plan, ProviderInfo? provider, DateOnly day, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<(string Schedule, string TariffCode), TariffPrice>> Tariffs(IReadOnlyCollection<string> schedules, IReadOnlyCollection<string> tariffCodes,
        DateOnly day, CancellationToken cancellationToken);

    /// <summary>The code systems of active procedure and service codes (health.ref_procedure_and_service_codes).</summary>
    Task<IReadOnlyDictionary<string, string>> CodeSystems(IReadOnlyCollection<string> codes, DateOnly day, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<string>> KnownDiagnoses(IReadOnlyCollection<string> codes, DateOnly day, CancellationToken cancellationToken);

    Task<bool> ClaimFormatExists(string formatCode, DateOnly day, CancellationToken cancellationToken);

    Task<bool> RunCalendarExists(string runCode, DateOnly day, CancellationToken cancellationToken);

    Task<IReadOnlyList<FraudIndicator>> FraudIndicators(DateOnly day, CancellationToken cancellationToken);
}

internal static class Users
{
    public static Result<Guid> Require(IExecutionContext execution) =>
        execution.ActorType == ActorType.User && Guid.TryParse(execution.ActorId, out var id)
            ? id
            : Error.Forbidden(ErrorCodes.Forbidden, "This is done by an identified user.");
}

internal static class Numbers
{
    /// <summary>TODO(spec): numbers are a prefix and an ID-based suffix; sequences and check digits need a counter (OQ-058).</summary>
    public static string Of(string prefix, Guid id, int length) => References.Of(prefix, id, length);
}

public sealed record SchemeView(Guid Id, string SchemeNumber, string SponsorCode, SchemeStatus Status, DateOnly StartDate, DateOnly RenewalDate, string? Currency, long RowVersion)
{
    public static SchemeView From(HealthScheme s) => new(s.Id, s.SchemeNumber, s.SponsorCode, s.Status, s.StartDate, s.RenewalDate, s.Currency, s.RowVersion);
}

public sealed record DependantView(Guid Id, Guid PartyId, string DependantType, string Suffix, DateOnly StartDate, DateOnly? EndDate, [property: OneOf("ACTIVE", "REMOVED")] string Status);

/// <param name="ProductCode">The Product Factory product the membership is on, recorded at enrolment and on a plan change (GAP-050); null while its plan names none.</param>
/// <param name="ProductVersion">That product's version at the time; null with <paramref name="ProductCode"/>.</param>
public sealed record MembershipView(Guid Id, string MemberNumber, Guid PrincipalPartyId, Guid? SchemeId, string PlanCode, string OptionCode, int PlanVersion,
    string? ProductCode, int? ProductVersion, MembershipStatus Status,
    DateOnly StartDate, DateOnly? EndDate, string? MemberCategory, IReadOnlyList<WaitingPeriod> WaitingPeriods, IReadOnlyList<DependantView> Dependants, long RowVersion,
    [property: BuildingBlocks.Contracts.AlwaysPresent] IReadOnlyList<MembershipEventView>? Events = null, [property: BuildingBlocks.Contracts.AlwaysPresent] Guid? PayerPartyId = null, [property: BuildingBlocks.Contracts.AlwaysPresent] Guid? BillingAccountRef = null,
    [property: BuildingBlocks.Contracts.AlwaysPresent] string? Currency = null)
{
    /// <summary>BR-070: the membership's lifecycle events in date order, its payer, billing account and currency (BR-068).</summary>
    public static MembershipView From(HealthMembership m) => new(m.Id, m.MemberNumber, m.PrincipalPartyId, m.SchemeId, m.PlanCode, m.OptionCode, m.PlanVersion,
        m.ProductCode, m.ProductVersion, m.Status, m.StartDate, m.EndDate, m.MemberCategory, m.WaitingPeriods,
        m.Dependants.Select(d => new DependantView(d.Id, d.PartyId, d.DependantType, d.Suffix, d.StartDate, d.EndDate, d.Status)).ToList(), m.RowVersion,
        m.Events.OrderBy(e => e.EffectiveDate).Select(e => new MembershipEventView(e.EventType, e.EffectiveDate, e.Reason)).ToList(), m.PayerPartyId, m.BillingAccountRef,
        m.Currency);
}

/// <param name="EventType">ENROL, ADD_DEPENDANT, REMOVE_DEPENDANT, CHANGE_PLAN, SUSPEND, REINSTATE or TERMINATE.</param>
/// <param name="Reason">The reason given, or the dependant's suffix for a dependant's event.</param>
public sealed record MembershipEventView(string EventType, DateOnly EffectiveDate, string Reason);

public sealed record AccumulatorView(Guid Id, Guid BeneficiaryPartyId, string BenefitCategory, string Scope, DateOnly PeriodStart, DateOnly PeriodEnd, decimal? Limit,
    decimal Used, decimal Reserved, decimal? Remaining, decimal DeductibleMet, decimal CopayTaken, long RowVersion, [property: BuildingBlocks.Contracts.AlwaysPresent] string? Currency = null)
{
    /// <summary>BR-068: amounts are in the membership's currency.</summary>
    public static AccumulatorView From(BenefitAccumulator a, string? currency) => new(a.Id, a.BeneficiaryPartyId, a.BenefitCategory, a.Scope, a.PeriodStart, a.PeriodEnd,
        a.LimitAmount, a.UsedAmount, a.ReservedAmount, a.Remaining, a.DeductibleMet, a.CopayTaken, a.RowVersion, currency);
}

public sealed record EligibilityView(Guid Id, string MemberNumber, string DependantSuffix, string? ProviderId, string ServiceType, string ResponseCode,
    string Outcome, DateTimeOffset DecidedAt);

public sealed record PreauthLineView(Guid Id, string ProcedureCode, int Quantity, decimal AuthorisedAmount, string? BenefitCategory, decimal ReservedAmount,
    decimal ConsumedAmount);

public sealed record PreauthorisationView(Guid Id, string AuthNumber, Guid MembershipId, Guid PatientPartyId, string ProviderId, PreauthStatus Status, decimal RequestedAmount,
    decimal? AuthorisedAmount, DateOnly? ValidFrom, DateOnly? ValidTo, string? ServiceType, string? Conditions, string? DecisionReason,
    IReadOnlyList<PreauthLineView> Lines, long RowVersion, [property: BuildingBlocks.Contracts.AlwaysPresent] string? Currency = null, [property: BuildingBlocks.Contracts.AlwaysPresent] Guid? ClinicalReviewer = null,
    [property: BuildingBlocks.Contracts.AlwaysPresent] Guid? DecidedBy = null)
{
    /// <summary>
    /// BR-068, BR-072: amounts in the membership's currency; the clinical reviewer and the decider. The diagnosis codes are
    /// medical information (HLT-008): only the clinical read gives them (HealthGetAuthorisationClinical).
    /// </summary>
    public static PreauthorisationView From(Preauthorisation p, string? currency) => new(p.Id, p.AuthNumber, p.MembershipId, p.PatientPartyId, p.ProviderId, p.Status,
        p.RequestedAmount, p.AuthorisedAmount, p.ValidFrom, p.ValidTo, p.ServiceType, p.Conditions, p.DecisionReason,
        p.Lines.Select(l => new PreauthLineView(l.Id, l.ProcedureCode, l.Quantity, l.AuthorisedAmount, l.BenefitCategory, l.ReservedAmount, l.ConsumedAmount)).ToList(),
        p.RowVersion, currency, p.ClinicalReviewer, p.DecidedBy);
}

/// <summary>A preauthorisation's medical information (BR-072, HLT-008), for holders of the clinical review permission only.</summary>
public sealed record PreauthorisationClinicalView(Guid Id, string AuthNumber, [property: BuildingBlocks.Domain.CodeList(HealthCodeLists.Diagnoses, "code")] IReadOnlyList<string> DiagnosisCodes,
    Guid? ClinicalReviewer);

public sealed record ClaimLineView(int LineNo, string TariffCode, int Quantity, decimal ClaimedAmount, [property: BuildingBlocks.Domain.OneOf("PAY", "PEND", "REJECT")] string? Outcome, string? AdjustmentReason, string? BenefitCategory,
    string? NetworkStatus, decimal? TariffAmount, decimal AllowedAmount, decimal DeductibleAmount, decimal CopayAmount, decimal PaidAmount, decimal MemberLiability,
    Guid? PreauthLineId);

/// <summary>The claim without diagnosis codes: medical information is shown only to roles with a clinical purpose (HLT-008).</summary>
public sealed record MedicalClaimView(Guid Id, string ClaimNumber, string Source, string? ProviderId, Guid MembershipId, Guid PatientPartyId, DateOnly ServiceDate,
    MedicalClaimStatus Status, string Currency, decimal TotalClaimed, decimal? TotalPaid, string? PendReason, Guid? PaymentRunId, IReadOnlyList<ClaimLineView> Lines, long RowVersion,
    [property: BuildingBlocks.Domain.OneOf(MedicalClaimOutcomes.Approved, MedicalClaimOutcomes.PartlyApproved), BuildingBlocks.Contracts.AlwaysPresent] string? Outcome = null,
    [property: BuildingBlocks.Contracts.AlwaysPresent] string? DecisionReasonCode = null, [property: BuildingBlocks.Contracts.AlwaysPresent] Guid? ApprovedBy = null, [property: BuildingBlocks.Contracts.AlwaysPresent] DateTimeOffset? ApprovedAt = null)
{
    /// <summary>BR-079: who approved the claim, and when.</summary>
    public static MedicalClaimView From(MedicalClaim c) => new(c.Id, c.ClaimNumber, c.Source, c.ProviderId, c.MembershipId, c.PatientPartyId, c.ServiceDate,
        c.Status, c.Currency, c.TotalClaimed, c.TotalPaid, c.PendReason, c.PaymentRunId,
        c.Lines.OrderBy(l => l.LineNo).Select(l => new ClaimLineView(l.LineNo, l.TariffCode, l.Quantity, l.ClaimedAmount, l.Outcome, l.AdjustmentReason, l.BenefitCategory,
            l.NetworkStatus, l.TariffAmount, l.AllowedAmount, l.DeductibleAmount, l.CopayAmount, l.PaidAmount, l.MemberLiability, l.PreauthLineId)).ToList(), c.RowVersion,
        c.DecisionOutcome, c.DecisionReasonCode, c.ApprovedBy, c.ApprovedAt);
}

public sealed record UtilisationCaseView(Guid Id, Guid MembershipId, string RuleCode, UtilisationStatus Status, Guid? ReviewerId, string? Outcome, Guid? ClaimId, long RowVersion,
    [property: BuildingBlocks.Contracts.AlwaysPresent] DateTimeOffset? OpenedAt = null, [property: BuildingBlocks.Contracts.AlwaysPresent] DateTimeOffset? ConcludedAt = null)
{
    /// <summary>BR-079: when the case was opened and concluded.</summary>
    public static UtilisationCaseView From(UtilisationCase c) => new(c.Id, c.MembershipId, c.RuleCode, c.Status, c.ReviewerId, c.Outcome, c.ClaimId, c.RowVersion,
        c.OpenedAt, c.ConcludedAt);
}

public sealed record FraudCaseView(Guid Id, string SubjectType, string SubjectRef, IReadOnlyList<string> IndicatorCodes, FraudStatus Status, string? OutcomeCode,
    Guid? InvestigatorId, long RowVersion, [property: BuildingBlocks.Contracts.AlwaysPresent] DateTimeOffset? OpenedAt = null, [property: BuildingBlocks.Contracts.AlwaysPresent] DateTimeOffset? ConcludedAt = null)
{
    /// <summary>BR-079: when the case was opened and concluded.</summary>
    public static FraudCaseView From(HealthFraudCase c) => new(c.Id, c.SubjectType, c.SubjectRef, c.IndicatorCodes, c.Status, c.OutcomeCode, c.InvestigatorId,
        c.RowVersion, c.OpenedAt, c.ConcludedAt);
}

/// <param name="DocumentId">The remittance advice document, once produced (BR-079).</param>
public sealed record RemittanceView(Guid Id, string ProviderId, Guid ProviderPartyId, Guid BankAccountId, decimal Amount, int ClaimCount, Guid? PaymentId, DateOnly? PaidOn,
    [property: BuildingBlocks.Contracts.AlwaysPresent] Guid? DocumentId = null);

/// <param name="PartlyInScope">
/// True when the run also pays claims outside the caller's scope (F103): only the remittances whose claims are all in scope
/// are listed, and the count and total are of those, so no other branch's or product's figures show through the run.
/// </param>
public sealed record PaymentRunView(Guid Id, string RunCode, DateTimeOffset CutOff, RunStatus Status, string Currency, int ClaimCount, decimal TotalAmount,
    IReadOnlyList<RemittanceView> Remittances, long RowVersion, bool PartlyInScope, Workflow.Contracts.LatestRejection? LatestRejection = null,
    [property: BuildingBlocks.Contracts.AlwaysPresent] Guid? ApprovalId = null, [property: BuildingBlocks.Contracts.AlwaysPresent] Guid? ApprovedBy = null, [property: BuildingBlocks.Contracts.AlwaysPresent] DateTimeOffset? ReleasedAt = null)
{
    public static PaymentRunView From(ProviderPaymentRun r) => Of(r, r.Remittances, false);

    public static PaymentRunView Of(ProviderPaymentRun r, IReadOnlyList<RemittanceAdvice> visible, bool partly) => new(r.Id, r.RunCode, r.CutOff, r.Status, r.Currency,
        partly ? visible.Sum(x => x.ClaimCount) : r.ClaimCount, partly ? visible.Sum(x => x.Amount) : r.TotalAmount,
        visible.Select(x => new RemittanceView(x.Id, x.ProviderId, x.ProviderPartyId, x.BankAccountId, x.Amount, x.ClaimCount, x.PaymentId, x.PaidOn, x.DocumentId)).ToList(),
        r.RowVersion, partly, null, r.ApprovalId, r.ApprovedBy, r.ReleasedAt);
}

/// <summary>A membership's number, product and node: what scopes the claims a payment run pays (F103).</summary>
public sealed record MembershipScope(string MemberNumber, string? ProductCode, string? OrgNodeCode);

/// <summary>
/// What of a provider payment run a caller may see (F103): a remittance pays many members' claims, so it is in scope
/// only when all its claims are (by their memberships' product and node). Building or releasing a run needs all its claims in scope.
/// </summary>
public static class RunVisibility
{
    public static (IReadOnlyList<RemittanceAdvice> Visible, bool Partly) Of(IReadOnlyList<RemittanceAdvice> remittances, IReadOnlyList<MedicalClaim> claims,
        IReadOnlyDictionary<Guid, MembershipScope> memberships, AccessScope scope)
    {
        if (scope.IsUnrestricted)
        {
            return (remittances, false);
        }

        var visible = remittances.Where(r => claims.Where(c => Pays(r, c, memberships)).ToList() is { Count: > 0 } paid && paid.All(c => Covered(c, memberships, scope))).ToList();
        return (visible, visible.Count < remittances.Count);
    }

    public static bool CoversAll(IEnumerable<MedicalClaim> claims, IReadOnlyDictionary<Guid, MembershipScope> memberships, AccessScope scope) =>
        scope.IsUnrestricted || claims.All(c => Covered(c, memberships, scope));

    private static bool Covered(MedicalClaim claim, IReadOnlyDictionary<Guid, MembershipScope> memberships, AccessScope scope) =>
        memberships.TryGetValue(claim.MembershipId, out var m) && scope.Allows(m.ProductCode, m.OrgNodeCode);

    /// <summary>A member's own claims are remitted under their member number; a provider's under its provider ID.</summary>
    private static bool Pays(RemittanceAdvice remittance, MedicalClaim claim, IReadOnlyDictionary<Guid, MembershipScope> memberships) =>
        claim.Source == ClaimSources.Member
            ? memberships.TryGetValue(claim.MembershipId, out var m) && m.MemberNumber == remittance.ProviderId
            : claim.ProviderId == remittance.ProviderId;
}
