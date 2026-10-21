using System.Globalization;
using Insys.BuildingBlocks.Domain;

namespace Insys.Rating.Domain.Calculations;

/// <summary>A band of a rating factor: "18-24" (inclusive), "60+" (from 60), or a single value such as "G3".</summary>
public sealed record Band(string Label, decimal? From, decimal? To)
{
    public static Band Parse(string label)
    {
        var text = label.Trim();
        if (text.EndsWith('+') && TryNumber(text[..^1], out var from))
        {
            return new Band(text, from, null);
        }

        var dash = text.IndexOf('-', 1);
        return dash > 0 && TryNumber(text[..dash], out var low) && TryNumber(text[(dash + 1)..], out var high)
            ? new Band(text, low, high)
            : new Band(text, null, null);
    }

    public bool Contains(decimal value) => From is not null && value >= From && (To is null || value <= To);

    private static bool TryNumber(string text, out decimal value) =>
        decimal.TryParse(text.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out value);
}

/// <summary>rating.cfg_rating_factor_definitions: where a factor's value comes from in the inputs, and its bands.</summary>
public sealed record FactorDefinition(string FactorCode, string DataType, string SourceAttribute, IReadOnlyList<Band> Bands);

/// <summary>One cell of a rating table: a value for a combination of factor values, in the order of the table's dimensions.</summary>
public sealed record TableEntry(IReadOnlyList<string> Key, decimal Value);

/// <summary>rating.cfg_rating_tables.</summary>
public sealed record RatingTable(string TableCode, int Version, IReadOnlyList<string> Dimensions, IReadOnlyList<TableEntry> Entries, string Interpolation);

/// <summary>rating.cfg_premium_parameters for the product version: AMOUNT, PERCENT or FACTOR.</summary>
public sealed record PremiumParameter(string Code, decimal Value, string Unit);

/// <summary>rating.cfg_discount_and_loading_schedules.</summary>
public sealed record Adjustment(string Code, string Type, string Basis, decimal Value, decimal? Cap, int StackingOrder);

/// <summary>rating.cfg_premium_bounds for the product, currency and frequency.</summary>
public sealed record PremiumBounds(decimal Minimum, decimal? Maximum);

/// <summary>rating.cfg_premium_tax_and_levy_rates. Rate is a percentage.</summary>
public sealed record TaxRate(string TaxCode, decimal Rate, string Base, string RoundingRule);

/// <summary>rating.cfg_rounding_and_currency_precision_rules.</summary>
public sealed record Rounding(string RuleCode, decimal Step, string Method, string Stage)
{
    public decimal Apply(decimal value)
    {
        if (Step <= 0)
        {
            return value;
        }

        var units = value / Step;
        var rounded = Method switch
        {
            "HALF_UP" => Math.Round(units, 0, MidpointRounding.AwayFromZero),
            "HALF_EVEN" => Math.Round(units, 0, MidpointRounding.ToEven),
            "UP" => Math.Round(units, 0, MidpointRounding.ToPositiveInfinity),
            "DOWN" => Math.Round(units, 0, MidpointRounding.ToZero),
            _ => throw new InvalidOperationException($"Unknown rounding method {Method}."),
        };
        return rounded * Step;
    }
}

/// <summary>
/// The rating configuration in force for one product version, currency and day: what the reference engine reads
/// (ADR-007). Tables apply when the product version has a premium parameter with the table's code.
/// </summary>
public sealed record RatingConfiguration(
    string ProductCode,
    int ProductVersion,
    IReadOnlyList<FactorDefinition> Factors,
    IReadOnlyList<RatingTable> Tables,
    IReadOnlyList<PremiumParameter> Parameters,
    IReadOnlyList<Adjustment> Adjustments,
    IReadOnlyDictionary<string, PremiumBounds> BoundsByFrequency,
    IReadOnlyList<(string Jurisdiction, TaxRate Tax)> Taxes,
    IReadOnlyList<Rounding> Roundings)
{
    /// <summary>The product_version reference used by premium parameters and test cases, as in the catalogue sample "GEN-MOTOR-PV v4".</summary>
    public static string ProductVersionRef(string productCode, int version) => $"{productCode} v{version.ToString(CultureInfo.InvariantCulture)}";
}

public static class RatingErrorCodes
{
    [ErrorCode(422, "error.rating.input_missing")]
    public const string InputMissing = "RATING_INPUT_MISSING";

    [ErrorCode(422, "error.rating.configuration_incomplete")]
    public const string ConfigurationIncomplete = "RATING_CONFIG_INCOMPLETE";

    [ErrorCode(422, "error.rating.no_table_value")]
    public const string NoTableValue = "RATING_NO_TABLE_VALUE";
}
