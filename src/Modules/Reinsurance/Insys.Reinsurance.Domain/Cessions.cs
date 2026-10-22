using System.Globalization;
using Insys.BuildingBlocks.Domain;

namespace Insys.Reinsurance.Domain;

public static class TreatyTypes
{
    public const string QuotaShare = "QUOTA_SHARE";
    public const string Surplus = "SURPLUS";
    public const string ExcessOfLoss = "EXCESS_OF_LOSS";
    public const string StopLoss = "STOP_LOSS";

    /// <summary>Proportional treaties cede sum insured and premium; non-proportional ones only recover claims.</summary>
    public static bool IsProportional(string type) => type is QuotaShare or Surplus;
}

public static class AllocationBases
{
    public const string SumInsured = "SUM_INSURED";
    public const string Premium = "PREMIUM";
    public const string ProbableMaximumLoss = "PROBABLE_MAXIMUM_LOSS";
}

/// <summary>A treaty version in force (reinsurance.cfg_treaty_definitions); <paramref name="LeadReinsurer"/> is the first participant.</summary>
public sealed record TreatyTerms(string TreatyCode, int Version, string Type, string Retention, decimal Capacity, DateOnly PeriodFrom, DateOnly PeriodTo,
    string? LeadReinsurer);

/// <summary>A cession rule version in force (reinsurance.cfg_cession_rules): the treaty for its products.</summary>
public sealed record CessionRule(string RuleCode, int Version, string TreatyCode, IReadOnlyList<string> Products, string AllocationBasis);

/// <summary>A facultative arrangement (reinsurance.mst_facultative_arrangements) for one risk, by its reference (the policy number).</summary>
public sealed record FacultativeArrangement(string ArrangementCode, int Version, string RiskReference, string Reinsurer, decimal SharePct);

/// <summary>What a policy puts at risk: its sum insured and annual premium over its period.</summary>
public sealed record PolicyRisk(string SourceDomain, Guid PolicyRef, string PolicyNumber, string ProductCode, decimal SumInsured, decimal Premium, string Currency,
    DateOnly PeriodStart, DateOnly PeriodEnd);

public sealed record CessionCalculated(Guid CessionId, string TreatyCode, string SourceDomain, Guid PolicyRef, decimal CededSumInsured, decimal CededPremium,
    decimal PremiumChange, string Currency, DateOnly PeriodStart, DateOnly PeriodEnd, string RuleCode, int RuleVersion, Guid? SupersedesId, DateTimeOffset OccurredAt)
    : IDomainEvent;

/// <summary>
/// The treaty arithmetic (OQ-063 item 1, pending business sign-off). QUOTA_SHARE: the retention is the retained percent;
/// the ceded sum insured is the rest, at most the capacity. SURPLUS: the retention is an amount; the ceded sum insured is
/// what exceeds it, at most the capacity. The ceded premium takes the same share of the premium (SUM_INSURED and PREMIUM
/// bases alike). PROBABLE_MAXIMUM_LOSS has no data and is refused. Amounts round half away from zero to cents.
/// </summary>
public static class TreatyMath
{
    public static Result<decimal> CededShare(TreatyTerms treaty, string allocationBasis, decimal sumInsured)
    {
        if (allocationBasis == AllocationBases.ProbableMaximumLoss)
        {
            return Error.Validation(ReinsuranceErrorCodes.TreatyNotSupported, "Cession on probable maximum loss is not supported: there is no PML data (OQ-063).");
        }

        if (!decimal.TryParse(treaty.Retention.Replace("%", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal), NumberStyles.Number,
                CultureInfo.InvariantCulture, out var retention) || retention < 0)
        {
            return Error.Validation(ReinsuranceErrorCodes.TreatyNotSupported, $"Treaty {treaty.TreatyCode}'s retention {treaty.Retention} is not a number.");
        }

        if (sumInsured <= 0)
        {
            return 0m;
        }

        decimal ceded;
        switch (treaty.Type)
        {
            case TreatyTypes.QuotaShare when retention <= 100:
                ceded = Math.Min(sumInsured * (100m - retention) / 100m, treaty.Capacity);
                break;
            case TreatyTypes.Surplus:
                ceded = Math.Min(Math.Max(sumInsured - retention, 0m), treaty.Capacity);
                break;
            default:
                return Error.Validation(ReinsuranceErrorCodes.TreatyNotSupported, $"Treaty {treaty.TreatyCode} ({treaty.Type}, retention {treaty.Retention}) does not cede proportionally.");
        }

        return Math.Round(ceded / sumInsured, 6, MidpointRounding.AwayFromZero);
    }

    /// <summary>EXCESS_OF_LOSS, per claim: the paid amount above the retention, at most the capacity.</summary>
    public static Result<decimal> ExcessRecoverable(TreatyTerms treaty, decimal paid)
    {
        if (treaty.Type != TreatyTypes.ExcessOfLoss
            || !decimal.TryParse(treaty.Retention, NumberStyles.Number, CultureInfo.InvariantCulture, out var retention))
        {
            return Error.Validation(ReinsuranceErrorCodes.TreatyNotSupported, $"Treaty {treaty.TreatyCode} is not an excess of loss treaty with an amount retention.");
        }

        return Money(Math.Min(Math.Max(paid - retention, 0m), treaty.Capacity));
    }

    public static decimal Money(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);
}

/// <summary>
/// The Cession aggregate (reinsurance.cession): one policy's share ceded under one treaty (or facultative arrangement).
/// Immutable once written: a recalculation is a new cession superseding it (RecalculateCession). It is reproducible
/// from the treaty, rule and arrangement versions and the gross amounts it records (REI-002), and linked to its policy
/// (REI-001).
/// </summary>
public sealed class Cession : AggregateRoot<Guid>
{
    private Cession()
    {
    }

    public string TreatyCode { get; private set; } = "";

    public int? TreatyVersion { get; private set; }

    public string? ArrangementCode { get; private set; }

    public string SourceType { get; private set; } = "";

    public Guid SourceId { get; private set; }

    public string ProductCode { get; private set; } = "";

    public DateOnly PeriodStart { get; private set; }

    public DateOnly PeriodEnd { get; private set; }

    public decimal GrossSumInsured { get; private set; }

    public decimal GrossPremium { get; private set; }

    public decimal CededShare { get; private set; }

    public decimal CededSumInsured { get; private set; }

    public decimal CededPremium { get; private set; }

    public string Currency { get; private set; } = "";

    public string RuleCode { get; private set; } = "";

    public int RuleVersion { get; private set; }

    public DateTimeOffset CalculatedAt { get; private set; }

    public Guid? SupersedesId { get; private set; }

    /// <summary>The policy's domain, as the cession's source type (GENERAL_POLICY, LIFE_POLICY).</summary>
    public string SourceDomain => SourceType.Split('_')[0];

    public static string SourceTypeOf(string sourceDomain) => sourceDomain + "_POLICY";

    /// <summary>Command CalculateCession under a treaty, through its cession rule; nothing is ceded when the share is zero.</summary>
    public static Result<Cession> CalculateCession(Guid id, PolicyRisk risk, TreatyTerms treaty, CessionRule rule, Cession? superseded, DateTimeOffset now)
    {
        var share = TreatyMath.CededShare(treaty, rule.AllocationBasis, risk.SumInsured);
        return share.IsFailure ? share.Error : Create(id, risk, treaty.TreatyCode, treaty.Version, null, rule.RuleCode, rule.Version, share.Value, superseded, now);
    }

    /// <summary>A facultative arrangement for the risk cedes its share instead of the treaty (OQ-063 item 1).</summary>
    public static Result<Cession> CalculateFacultative(Guid id, PolicyRisk risk, FacultativeArrangement arrangement, Cession? superseded, DateTimeOffset now) =>
        Create(id, risk, arrangement.ArrangementCode, null, arrangement.ArrangementCode, "FACULTATIVE", arrangement.Version, arrangement.SharePct / 100m, superseded, now);

    private static Result<Cession> Create(Guid id, PolicyRisk risk, string treatyCode, int? treatyVersion, string? arrangementCode, string ruleCode, int ruleVersion,
        decimal share, Cession? superseded, DateTimeOffset now)
    {
        var cededSumInsured = TreatyMath.Money(risk.SumInsured * share);
        var cededPremium = TreatyMath.Money(risk.Premium * share);
        if (cededSumInsured <= 0 && superseded is null)
        {
            return Error.Conflict(ReinsuranceErrorCodes.NothingCeded, $"Nothing of {risk.PolicyNumber} is ceded under {treatyCode}.");
        }

        var cession = new Cession
        {
            Id = Guard.NotEmpty(id), TreatyCode = Guard.MaxLength(treatyCode, 15), TreatyVersion = treatyVersion, ArrangementCode = arrangementCode,
            SourceType = SourceTypeOf(risk.SourceDomain), SourceId = risk.PolicyRef, ProductCode = Guard.MaxLength(risk.ProductCode, 15), PeriodStart = risk.PeriodStart,
            PeriodEnd = risk.PeriodEnd, GrossSumInsured = risk.SumInsured, GrossPremium = risk.Premium, CededShare = share, CededSumInsured = cededSumInsured,
            CededPremium = cededPremium, Currency = risk.Currency, RuleCode = Guard.MaxLength(ruleCode, 15), RuleVersion = ruleVersion, CalculatedAt = now,
            SupersedesId = superseded?.Id,
        };
        cession.Raise(new CessionCalculated(cession.Id, cession.TreatyCode, risk.SourceDomain, risk.PolicyRef, cededSumInsured, cededPremium,
            cededPremium - (superseded?.CededPremium ?? 0m), risk.Currency, risk.PeriodStart, risk.PeriodEnd, cession.RuleCode, ruleVersion, superseded?.Id, now));
        return cession;
    }
}
