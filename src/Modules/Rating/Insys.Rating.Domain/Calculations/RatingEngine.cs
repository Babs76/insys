using System.Globalization;
using Insys.BuildingBlocks.Domain;

namespace Insys.Rating.Domain.Calculations;

/// <summary>One step of a calculation. <paramref name="Amount"/> is the premium after the step.</summary>
public sealed record BreakdownLine(int LineNo, string Component, string? FactorCode, string? FactorValue, decimal Amount);

/// <param name="RatingVersion">The highest version among the tables used (PRD-008); 0 when no table was used.</param>
public sealed record PremiumBreakdown(decimal Premium, string Currency, int RatingVersion, IReadOnlyList<BreakdownLine> Lines);

/// <param name="Values">
/// The rating inputs by dotted name ("risk.driver.age"): decimals, strings, booleans, or lists of strings such as
/// "adjustments". The application flattens nested JSON into these names.
/// </param>
public sealed record RatingInput(string Currency, string Frequency, IReadOnlyDictionary<string, object?> Values);

/// <summary>
/// The reference rating engine (ADR-007). Deterministic: the same inputs and configuration give the same breakdown
/// (LIF-002). Steps, each recorded as a line:
/// <list type="number">
/// <item>Exposure: sum_insured, sum_assured or exposure from the inputs, when the product version uses any table.</item>
/// <item>Tables: the exposure times the value each table holds for the factor values (banded where the factor has bands).</item>
/// <item>FACTOR parameters multiply; PERCENT parameters load by that percentage.</item>
/// <item>Discounts and loadings named in the input "adjustments", by stacking order, each type capped at its cap percentage.</item>
/// <item>AMOUNT parameters (fees) are added, then the premium bounds for the frequency apply.</item>
/// <item>Rounding at stage FINAL_PREMIUM, then taxes and levies for the input "jurisdiction", each rounded by its own rule.</item>
/// </list>
/// </summary>
public static class RatingEngine
{
    private const int Precision = 6;
    private static readonly string[] ExposureInputs = ["sum_insured", "sum_assured", "exposure"];

    public static Result<PremiumBreakdown> Calculate(RatingConfiguration config, RatingInput input)
    {
        try
        {
            return Run(config, input);
        }
        catch (RatingException ex)
        {
            return Error.Validation(ex.Code, ex.Message);
        }
    }

    private static PremiumBreakdown Run(RatingConfiguration config, RatingInput input)
    {
        if (config.Parameters.Count == 0)
        {
            throw new RatingException(RatingErrorCodes.ConfigurationIncomplete,
                $"No premium parameters are in force for {RatingConfiguration.ProductVersionRef(config.ProductCode, config.ProductVersion)}.");
        }

        var finalRounding = config.Roundings.FirstOrDefault(r => r.Stage == "FINAL_PREMIUM")
            ?? throw new RatingException(RatingErrorCodes.ConfigurationIncomplete, $"No final premium rounding rule is in force for {input.Currency}.");
        var eachFactor = config.Roundings.FirstOrDefault(r => r.Stage == "EACH_FACTOR");
        var lines = new List<BreakdownLine>();
        decimal premium = 0;

        void Line(string component, string? code, string? value, decimal amount)
        {
            premium = Math.Round(eachFactor?.Apply(amount) ?? amount, Precision, MidpointRounding.ToEven);
            lines.Add(new BreakdownLine(lines.Count + 1, component, code, value is { Length: > 40 } ? value[..40] : value, premium));
        }

        var tableCodes = config.Parameters.Select(p => p.Code).ToHashSet(StringComparer.Ordinal);
        var tables = config.Tables.Where(t => tableCodes.Contains(t.TableCode)).OrderBy(t => t.TableCode, StringComparer.Ordinal).ToList();
        if (tables.Count > 0)
        {
            var (name, exposure) = Exposure(input.Values);
            Line("EXPOSURE", null, name, exposure);
            foreach (var table in tables)
            {
                var key = table.Dimensions.Select(d => FactorValue(config, d, input.Values)).ToList();
                Line("TABLE", table.TableCode, string.Join(",", key), premium * Lookup(table, key));
            }
        }

        var schedules = config.Adjustments.Select(a => a.Code).ToHashSet(StringComparer.Ordinal);
        foreach (var parameter in config.Parameters.Where(p => !tables.Any(t => t.TableCode == p.Code) && !schedules.Contains(p.Code)).OrderBy(p => p.Code, StringComparer.Ordinal))
        {
            switch (parameter.Unit)
            {
                case "FACTOR":
                    Line("FACTOR", parameter.Code, Text(parameter.Value), premium * parameter.Value);
                    break;
                case "PERCENT":
                    Line("PERCENT", parameter.Code, Text(parameter.Value), premium * (1 + (parameter.Value / 100m)));
                    break;
            }
        }

        ApplyAdjustments(config, input.Values, premium, Line);

        foreach (var fee in config.Parameters.Where(p => p.Unit == "AMOUNT" && !schedules.Contains(p.Code)).OrderBy(p => p.Code, StringComparer.Ordinal))
        {
            Line("FEE", fee.Code, Text(fee.Value), premium + fee.Value);
        }

        if (config.BoundsByFrequency.TryGetValue(input.Frequency, out var bounds))
        {
            if (premium < bounds.Minimum)
            {
                Line("MINIMUM", null, Text(bounds.Minimum), bounds.Minimum);
            }
            else if (bounds.Maximum is { } maximum && premium > maximum)
            {
                Line("MAXIMUM", null, Text(maximum), maximum);
            }
        }

        Line("ROUNDING", finalRounding.RuleCode, Text(finalRounding.Step), finalRounding.Apply(premium));
        var net = premium;
        var jurisdiction = Optional(input.Values, "jurisdiction");
        foreach (var (_, tax) in config.Taxes.Where(t => jurisdiction is not null && t.Jurisdiction == jurisdiction).OrderBy(t => t.Tax.TaxCode, StringComparer.Ordinal))
        {
            if (tax.Base is not ("GROSS_PREMIUM" or "NET_PREMIUM"))
            {
                // TODO(spec): a levy on commission needs the commission, which rating does not know (OQ-052).
                throw new RatingException(RatingErrorCodes.ConfigurationIncomplete, $"Tax {tax.TaxCode} is on {tax.Base}, which rating cannot calculate.");
            }

            var rounding = config.Roundings.FirstOrDefault(r => r.RuleCode == tax.RoundingRule) ?? finalRounding;
            Line("TAX", tax.TaxCode, Text(tax.Rate), premium + rounding.Apply(net * tax.Rate / 100m));
        }

        return new PremiumBreakdown(finalRounding.Apply(premium), input.Currency, tables.Count == 0 ? 0 : tables.Max(t => t.Version), lines);
    }

    /// <summary>Discounts and loadings in stacking order; the total of each type stays within its cap, a percentage of the premium before adjustments.</summary>
    private static void ApplyAdjustments(RatingConfiguration config, IReadOnlyDictionary<string, object?> values, decimal before,
        Action<string, string?, string?, decimal> line)
    {
        var requested = Strings(values, "adjustments");
        var unknown = requested.FirstOrDefault(code => config.Adjustments.All(a => a.Code != code));
        if (unknown is not null)
        {
            throw new RatingException(RatingErrorCodes.ConfigurationIncomplete, $"Adjustment {unknown} is not in force.");
        }

        var running = before;
        var totals = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var adjustment in config.Adjustments.Where(a => requested.Contains(a.Code)).OrderBy(a => a.StackingOrder).ThenBy(a => a.Code, StringComparer.Ordinal))
        {
            var change = adjustment.Basis == "PERCENT" ? running * adjustment.Value / 100m : adjustment.Value;
            var total = totals.GetValueOrDefault(adjustment.Type);
            if (adjustment.Cap is { } cap)
            {
                change = Math.Max(0, Math.Min(change, (before * cap / 100m) - total));
            }

            totals[adjustment.Type] = total + change;
            running += adjustment.Type == "DISCOUNT" ? -change : change;
            line(adjustment.Type, adjustment.Code, Text(adjustment.Value), running);
        }
    }

    private static decimal Lookup(RatingTable table, IReadOnlyList<string> key)
    {
        var entry = table.Entries.FirstOrDefault(e => e.Key.SequenceEqual(key, StringComparer.Ordinal));
        if (entry is not null)
        {
            return entry.Value;
        }

        if (table.Interpolation == "LINEAR" && table.Dimensions.Count == 1 && Number(key[0]) is { } x)
        {
            var points = table.Entries.Select(e => (X: Number(e.Key[0]), e.Value)).Where(p => p.X is not null).Select(p => (X: p.X!.Value, p.Value))
                .OrderBy(p => p.X).ToList();
            var low = points.LastOrDefault(p => p.X <= x);
            var high = points.FirstOrDefault(p => p.X >= x);
            if (points.Count >= 2 && points[0].X <= x && points[^1].X >= x)
            {
                return high.X == low.X ? low.Value : low.Value + ((high.Value - low.Value) * (x - low.X) / (high.X - low.X));
            }
        }

        throw new RatingException(RatingErrorCodes.NoTableValue, $"Table {table.TableCode} has no value for [{string.Join(", ", key)}].");
    }

    private static string FactorValue(RatingConfiguration config, string factorCode, IReadOnlyDictionary<string, object?> values)
    {
        var factor = config.Factors.FirstOrDefault(f => f.FactorCode == factorCode)
            ?? throw new RatingException(RatingErrorCodes.ConfigurationIncomplete, $"Table dimension {factorCode} is not a rating factor in force.");
        var raw = values.GetValueOrDefault(factor.SourceAttribute) switch
        {
            null => throw new RatingException(RatingErrorCodes.InputMissing, $"Input {factor.SourceAttribute} (factor {factorCode}) is missing."),
            decimal d => d.ToString(CultureInfo.InvariantCulture),
            bool b => b ? "true" : "false",
            var other => other.ToString() ?? "",
        };
        if (factor.Bands.Count == 0)
        {
            return raw;
        }

        var band = Number(raw) is { } n ? factor.Bands.FirstOrDefault(b => b.Contains(n)) : factor.Bands.FirstOrDefault(b => b.Label == raw);
        return band?.Label ?? throw new RatingException(RatingErrorCodes.NoTableValue, $"Input {factor.SourceAttribute} is in no band of factor {factorCode}.");
    }

    private static (string Name, decimal Value) Exposure(IReadOnlyDictionary<string, object?> values)
    {
        foreach (var name in ExposureInputs)
        {
            if (values.GetValueOrDefault(name) is decimal value)
            {
                return (name, value);
            }
        }

        throw new RatingException(RatingErrorCodes.InputMissing, "The exposure (sum_insured, sum_assured or exposure) is missing.");
    }

    private static string? Optional(IReadOnlyDictionary<string, object?> values, string name) => values.GetValueOrDefault(name) as string;

    private static HashSet<string> Strings(IReadOnlyDictionary<string, object?> values, string name) =>
        values.GetValueOrDefault(name) is IEnumerable<string> list ? list.ToHashSet(StringComparer.Ordinal) : [];

    private static decimal? Number(string text) => decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static string Text(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private sealed class RatingException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }
}
