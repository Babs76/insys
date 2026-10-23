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
    Task<HealthScheme?> Scheme(Guid id, CancellationToken cancellationToken);

    Task<HealthMembership?> Membership(Guid id, CancellationToken cancellationToken);

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

    Task<IReadOnlyList<Guid>> SchemesToActivate(DateOnly day, int limit, CancellationToken cancellationToken);

    void Add(HealthScheme scheme);

    void Add(HealthMembership membership);

    void Add(BenefitAccumulator accumulator);

    void Add(EligibilityDecision decision);

    void Add(Preauthorisation preauthorisation);

    void Add(MedicalClaim claim);

    void Add(UtilisationCase utilisationCase);

    void Add(HealthFraudCase fraudCase);

    void Add(ProviderPaymentRun run);
}

/// <summary>A plan option in force and the version of its row.</summary>
public sealed record PlanDefinition(PlanTerms Terms, int Version);

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
    Task<string?> MemberNumberPrefix(DateOnly day, CancellationToken cancellationToken);

    Task<PlanDefinition?> Plan(string planCode, string optionCode, DateOnly day, CancellationToken cancellationToken);

    Task<SchemeCategory?> SchemeCategory(string categoryCode, DateOnly day, CancellationToken cancellationToken);

    /// <summary>The monthly contribution per member for a scheme's category, and the employer's share (health.cfg_contribution_rules).</summary>
    Task<ContributionRule?> Contribution(string schemeNumber, string categoryCode, DateOnly day, CancellationToken cancellationToken);

    /// <summary>The sponsor's party (health.mst_sponsor_register.party), when it holds a party id.</summary>
    Task<Guid?> SponsorParty(string sponsorCode, DateOnly day, CancellationToken cancellationToken);

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

public sealed record SchemeView(Guid Id, string SchemeNumber, string SponsorCode, string Status, DateOnly StartDate, DateOnly RenewalDate, string? Currency, long RowVersion)
{
    public static SchemeView From(HealthScheme s) => new(s.Id, s.SchemeNumber, s.SponsorCode, s.Status.ToString(), s.StartDate, s.RenewalDate, s.Currency, s.RowVersion);
}

public sealed record DependantView(Guid Id, Guid PartyId, string DependantType, string Suffix, DateOnly StartDate, DateOnly? EndDate, string Status);

public sealed record MembershipView(Guid Id, string MemberNumber, Guid PrincipalPartyId, Guid? SchemeId, string PlanCode, string OptionCode, int PlanVersion, string Status,
    DateOnly StartDate, DateOnly? EndDate, string? MemberCategory, IReadOnlyList<WaitingPeriod> WaitingPeriods, IReadOnlyList<DependantView> Dependants, long RowVersion)
{
    public static MembershipView From(HealthMembership m) => new(m.Id, m.MemberNumber, m.PrincipalPartyId, m.SchemeId, m.PlanCode, m.OptionCode, m.PlanVersion,
        m.Status.ToString(), m.StartDate, m.EndDate, m.MemberCategory, m.WaitingPeriods,
        m.Dependants.Select(d => new DependantView(d.Id, d.PartyId, d.DependantType, d.Suffix, d.StartDate, d.EndDate, d.Status)).ToList(), m.RowVersion);
}

public sealed record AccumulatorView(Guid Id, Guid BeneficiaryPartyId, string BenefitCategory, string Scope, DateOnly PeriodStart, DateOnly PeriodEnd, decimal? Limit,
    decimal Used, decimal Reserved, decimal? Remaining, decimal DeductibleMet, decimal CopayTaken, long RowVersion)
{
    public static AccumulatorView From(BenefitAccumulator a) => new(a.Id, a.BeneficiaryPartyId, a.BenefitCategory, a.Scope, a.PeriodStart, a.PeriodEnd, a.LimitAmount,
        a.UsedAmount, a.ReservedAmount, a.Remaining, a.DeductibleMet, a.CopayTaken, a.RowVersion);
}

public sealed record EligibilityView(Guid Id, string MemberNumber, string DependantSuffix, string? ProviderId, string ServiceType, string ResponseCode,
    string Outcome, DateTimeOffset DecidedAt);

public sealed record PreauthLineView(Guid Id, string ProcedureCode, int Quantity, decimal AuthorisedAmount, string? BenefitCategory, decimal ReservedAmount,
    decimal ConsumedAmount);

public sealed record PreauthorisationView(Guid Id, string AuthNumber, Guid MembershipId, Guid PatientPartyId, string ProviderId, string Status, decimal RequestedAmount,
    decimal? AuthorisedAmount, DateOnly? ValidFrom, DateOnly? ValidTo, string? ServiceType, string? Conditions, string? DecisionReason,
    IReadOnlyList<PreauthLineView> Lines, long RowVersion)
{
    public static PreauthorisationView From(Preauthorisation p) => new(p.Id, p.AuthNumber, p.MembershipId, p.PatientPartyId, p.ProviderId, p.Status.ToString(),
        p.RequestedAmount, p.AuthorisedAmount, p.ValidFrom, p.ValidTo, p.ServiceType, p.Conditions, p.DecisionReason,
        p.Lines.Select(l => new PreauthLineView(l.Id, l.ProcedureCode, l.Quantity, l.AuthorisedAmount, l.BenefitCategory, l.ReservedAmount, l.ConsumedAmount)).ToList(),
        p.RowVersion);
}

public sealed record ClaimLineView(int LineNo, string TariffCode, int Quantity, decimal ClaimedAmount, string? Outcome, string? AdjustmentReason, string? BenefitCategory,
    string? NetworkStatus, decimal? TariffAmount, decimal AllowedAmount, decimal DeductibleAmount, decimal CopayAmount, decimal PaidAmount, decimal MemberLiability,
    Guid? PreauthLineId);

/// <summary>The claim without diagnosis codes: medical information is shown only to roles with a clinical purpose (HLT-008).</summary>
public sealed record MedicalClaimView(Guid Id, string ClaimNumber, string Source, string? ProviderId, Guid MembershipId, Guid PatientPartyId, DateOnly ServiceDate,
    string Status, string Currency, decimal TotalClaimed, decimal? TotalPaid, string? PendReason, Guid? PaymentRunId, IReadOnlyList<ClaimLineView> Lines, long RowVersion)
{
    public static MedicalClaimView From(MedicalClaim c) => new(c.Id, c.ClaimNumber, c.Source, c.ProviderId, c.MembershipId, c.PatientPartyId, c.ServiceDate,
        c.Status.ToString(), c.Currency, c.TotalClaimed, c.TotalPaid, c.PendReason, c.PaymentRunId,
        c.Lines.OrderBy(l => l.LineNo).Select(l => new ClaimLineView(l.LineNo, l.TariffCode, l.Quantity, l.ClaimedAmount, l.Outcome, l.AdjustmentReason, l.BenefitCategory,
            l.NetworkStatus, l.TariffAmount, l.AllowedAmount, l.DeductibleAmount, l.CopayAmount, l.PaidAmount, l.MemberLiability, l.PreauthLineId)).ToList(), c.RowVersion);
}

public sealed record UtilisationCaseView(Guid Id, Guid MembershipId, string RuleCode, string Status, Guid? ReviewerId, string? Outcome, Guid? ClaimId, long RowVersion)
{
    public static UtilisationCaseView From(UtilisationCase c) => new(c.Id, c.MembershipId, c.RuleCode, c.Status.ToString(), c.ReviewerId, c.Outcome, c.ClaimId, c.RowVersion);
}

public sealed record FraudCaseView(Guid Id, string SubjectType, string SubjectRef, IReadOnlyList<string> IndicatorCodes, string Status, string? OutcomeCode,
    Guid? InvestigatorId, long RowVersion)
{
    public static FraudCaseView From(HealthFraudCase c) => new(c.Id, c.SubjectType, c.SubjectRef, c.IndicatorCodes, c.Status.ToString(), c.OutcomeCode, c.InvestigatorId,
        c.RowVersion);
}

public sealed record RemittanceView(Guid Id, string ProviderId, Guid ProviderPartyId, Guid BankAccountId, decimal Amount, int ClaimCount, Guid? PaymentId, DateOnly? PaidOn);

public sealed record PaymentRunView(Guid Id, string RunCode, DateTimeOffset CutOff, string Status, string Currency, int ClaimCount, decimal TotalAmount,
    IReadOnlyList<RemittanceView> Remittances, long RowVersion)
{
    public static PaymentRunView From(ProviderPaymentRun r) => new(r.Id, r.RunCode, r.CutOff, r.Status.ToString(), r.Currency, r.ClaimCount, r.TotalAmount,
        r.Remittances.Select(x => new RemittanceView(x.Id, x.ProviderId, x.ProviderPartyId, x.BankAccountId, x.Amount, x.ClaimCount, x.PaymentId, x.PaidOn)).ToList(),
        r.RowVersion);
}
