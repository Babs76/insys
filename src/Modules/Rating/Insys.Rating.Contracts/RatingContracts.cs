/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Rating
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
using System.Text.Json;
using Insys.BuildingBlocks.Contracts;

namespace Insys.Rating.Contracts;

public static class RatingPermissions
{
    public const string CalculationRead = "premium_calculation.read";
    public const string CalculationCreate = "premium_calculation.create";
    public const string Simulate = "premium_calculation.simulate";
}

/// <summary>A premium was calculated and recorded (PRD-008: with the product and rating versions used).</summary>
[IntegrationEvent("rating.PremiumCalculated")]
public sealed record PremiumCalculated(Guid CalculationId, string RequestRef, string SourceDomain, string SubjectType, Guid SubjectId, string ProductCode,
    int ProductVersion, int RatingVersion, decimal Premium, string Currency) : IIntegrationEvent;

/// <summary>
/// A calculation another module asked for through an event could not be made (BR-095): nothing is recorded, and the
/// subject is told why, with Rating's error code (for example a missing rating input).
/// </summary>
[IntegrationEvent("rating.PremiumNotCalculated")]
public sealed record PremiumNotCalculated(string RequestRef, string SourceDomain, string SubjectType, Guid SubjectId, string ErrorCode, string Reason) : IIntegrationEvent;

/// <param name="RatingDate">The day whose configuration applies; today when omitted. Recalculating as at the same day reproduces the result.</param>
/// <param name="Values">The rating inputs as a JSON object.</param>
public sealed record RatingRequest(string ProductCode, int ProductVersion, string Currency, string Frequency, DateOnly? RatingDate, JsonElement Values);

public sealed record SimulatedPremium(bool Succeeded, decimal? Premium, string Currency, int RatingVersion, string? ErrorCode, string? ErrorMessage);

/// <summary>One case of the rating test case library, run against a product version.</summary>
public sealed record RatingTestCaseResult(string CaseId, decimal ExpectedPremium, decimal Tolerance, decimal? ActualPremium, bool Passed, string? ErrorCode);

/// <summary>A tax a product's invoices carry (rating.cfg_premium_tax_and_levy_rates with a product, OQ-156). The rate is a percentage.</summary>
public sealed record ProductTaxRate(string TaxCode, decimal Rate);

/// <summary>Public query interface: the tax on a product's invoices on a day, or null when it carries none. Rating's rates are the one source (OQ-058).</summary>
public interface IProductTaxRates
{
    Task<ProductTaxRate?> ForProduct(Guid tenantId, string productCode, DateOnly day, CancellationToken cancellationToken);
}

/// <summary>
/// Public query interface: dry-run rating without recording anything, for Product Factory's simulation before
/// publication (PRD-005). Runs on Rating's own connection, for the current tenant.
/// </summary>
public interface IPremiumSimulator
{
    Task<SimulatedPremium> Simulate(RatingRequest request, CancellationToken cancellationToken);

    /// <summary>Runs every case of the rating test case library for the product version.</summary>
    Task<IReadOnlyList<RatingTestCaseResult>> RunTestCases(string productCode, int productVersion, DateOnly day, CancellationToken cancellationToken);
}
