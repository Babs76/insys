namespace Insys.ProductFactory.Contracts;

/// <summary>A limit, sublimit, deductible or excess of a coverage (product.cfg_limit_and_deductible_schedules, §7.2).</summary>
public sealed record ProductLimit(string ScheduleCode, string CoverageCode, string LimitType, decimal? Amount, decimal? Percentage, string Rule, string Currency);

/// <summary>
/// A product version that can be sold: its domain, line (the family, or class where set), coverages and their limits,
/// and when cover starts (ISSUE or FIRST_PAYMENT).
/// </summary>
public sealed record SellableProduct(string ProductCode, int Version, string Domain, string Line, IReadOnlyList<string> Coverages, IReadOnlyList<ProductLimit> Limits,
    string CoverStartsOn = "ISSUE", string? EligibilityRulesetCode = null, string ReinstatementArrears = "NONE");

/// <summary>A document template assigned to a product (product.cfg_product_template_assignments).</summary>
public sealed record ProductTemplate(string DocumentType, string TemplateCode, string Language);

/// <summary>
/// Public query interface (Published Language, domain-model.json product → domains): the product versions a domain
/// can sell, read live so publication and withdrawal take effect at once. Runs on Product Factory's own connection, for
/// the current tenant.
/// </summary>
public interface ISellableProducts
{
    /// <summary>
    /// The version, or the latest version when <paramref name="version"/> is null, if it is published or effective on
    /// the day and within any availability rule's sale window; otherwise null.
    /// </summary>
    Task<SellableProduct?> Sellable(Guid tenantId, string productCode, int? version, DateOnly day, CancellationToken cancellationToken);

    /// <summary>The version as published, for a policy already issued on it, whether or not it is still on sale.</summary>
    Task<SellableProduct?> Version(Guid tenantId, string productCode, int version, DateOnly day, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProductTemplate>> Templates(Guid tenantId, string productCode, DateOnly day, CancellationToken cancellationToken);
}

/// <summary>An exclusion's diagnosis and procedure codes or ranges (product.cfg_exclusion_library, ADR-012).</summary>
public sealed record ExclusionCodes(string Code, string Name, IReadOnlyList<string> DiagnosisCodes, IReadOnlyList<string> ProcedureCodes);

/// <summary>
/// Public query interface: the exclusions in force that carry codes, read live on Product Factory's own connection for
/// the current tenant. With <c>codes</c>, only those exclusions; without, every exclusion with codes.
/// </summary>
public interface IExclusionCodes
{
    Task<IReadOnlyList<ExclusionCodes>> InForce(Guid tenantId, IReadOnlyCollection<string>? codes, DateOnly day, CancellationToken cancellationToken);
}
