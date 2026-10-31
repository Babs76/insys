/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : ProductFactory
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

/// <summary>
/// Public query interface: the domain (LIFE, GENERAL, HEALTH) of each product code the tenant has defined, in any status
/// (draft to published), by its latest version. For checks of configuration that names products before they are on
/// sale, such as Health plans (addenda/health-plan-products.md). Codes with no definition are left out.
/// </summary>
public interface IProductDomains
{
    Task<IReadOnlyDictionary<string, string>> DomainsOf(Guid tenantId, IReadOnlyCollection<string> productCodes, CancellationToken cancellationToken);
}

/// <summary>The product versions published and sellable in a tenant on a day (onboarding readiness PRODUCT_FACTORY, RATING).</summary>
public interface IPublishedProducts
{
    Task<IReadOnlyList<(string ProductCode, int Version)>> Sellable(Guid tenantId, DateOnly day, CancellationToken cancellationToken);

    /// <summary>The countries the product's availability rules in force sell it in; none when no rule names any.</summary>
    Task<IReadOnlyList<string>> SaleCountries(Guid tenantId, string productCode, DateOnly day, CancellationToken cancellationToken);
}
