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

/// <param name="RatingDate">The day whose configuration applies; today when omitted. Recalculating as at the same day reproduces the result.</param>
/// <param name="Values">The rating inputs as a JSON object.</param>
public sealed record RatingRequest(string ProductCode, int ProductVersion, string Currency, string Frequency, DateOnly? RatingDate, JsonElement Values);

public sealed record SimulatedPremium(bool Succeeded, decimal? Premium, string Currency, int RatingVersion, string? ErrorCode, string? ErrorMessage);

/// <summary>One case of the rating test case library, run against a product version.</summary>
public sealed record RatingTestCaseResult(string CaseId, decimal ExpectedPremium, decimal Tolerance, decimal? ActualPremium, bool Passed, string? ErrorCode);

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
