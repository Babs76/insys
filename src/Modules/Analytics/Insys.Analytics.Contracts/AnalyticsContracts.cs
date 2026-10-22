namespace Insys.Analytics.Contracts;

public static class AnalyticsPermissions
{
    public const string ProjectionRead = "analytics.read";
    public const string ProjectionUpdate = "analytics.update";
}

/// <summary>
/// Public query interface: a metric's total over a period from the projections (analytics.cfg_metric_definitions: the
/// metric's source names the projection and measure). Read on Analytics' own connection. TODO(spec): metric formulas
/// are free text and are not evaluated; a metric is the sum of its source measure (OQ-064).
/// </summary>
public interface IProjectionTotals
{
    /// <summary>Null when the metric is not in force or its source is not a projection measure.</summary>
    Task<decimal?> Total(Guid tenantId, string metricCode, DateOnly from, DateOnly to, CancellationToken cancellationToken);
}

/// <summary>The projection measures a metric's source may name.</summary>
public static class ProjectionSources
{
    public const string WrittenPremium = "fact_written_premium.written_premium";
    public const string PolicyCount = "fact_written_premium.policy_count";
    public const string ClaimsIncurred = "fact_claims_incurred.incurred";
    public const string ClaimCount = "fact_claims_incurred.claim_count";

    public static readonly IReadOnlyList<string> All = [WrittenPremium, PolicyCount, ClaimsIncurred, ClaimCount];
}
