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
/// <item>Then, in the product version's step order (<see cref="StepOrder"/>; by default the order listed here): FACTOR
/// parameters multiply; PERCENT parameters load by that percentage; discounts and loadings named in the input
/// "adjustments" apply by stacking order, each type capped at its cap percentage; AMOUNT parameters (fees) are added;
/// the premium bounds for the frequency apply.</item>
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
        var parameters = config.Parameters
            .Where(p => !tables.Any(t => t.TableCode == p.Code) && !schedules.Contains(p.Code) && !p.Code.StartsWith(StepOrder.Prefix, StringComparison.Ordinal))
            .OrderBy(p => p.Code, StringComparer.Ordinal).ToList();
        foreach (var step in StepOrder.For(config.Parameters))
        {
            switch (step)
            {
                case RatingStep.Factors:
                    foreach (var factor in parameters.Where(p => p.Unit == "FACTOR"))
                    {
                        Line("FACTOR", factor.Code, Text(factor.Value), premium * factor.Value);
                    }

                    break;
                case RatingStep.Percent:
                    foreach (var loading in parameters.Where(p => p.Unit == "PERCENT"))
                    {
                        Line("PERCENT", loading.Code, Text(loading.Value), premium * (1 + (loading.Value / 100m)));
                    }

                    break;
                case RatingStep.Adjustments:
                    ApplyAdjustments(config, input.Values, premium, Line);
                    break;
                case RatingStep.Fees:
                    foreach (var fee in parameters.Where(p => p.Unit == "AMOUNT"))
                    {
                        Line("FEE", fee.Code, Text(fee.Value), premium + fee.Value);
                    }

                    break;
                case RatingStep.Bounds:
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

                    break;
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

    internal sealed class RatingException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }
}

/// <summary>The steps whose order a product version can configure. Tables (which need the exposure) come first, rounding and tax last.</summary>
public enum RatingStep
{
    Factors,
    Percent,
    Adjustments,
    Fees,
    Bounds,
}

/// <summary>
/// The step order of a product version (Phase 2 review item 5; OQ-052): premium parameters STEP_FACTORS,
/// STEP_PERCENT, STEP_ADJUSTMENTS, STEP_FEES and STEP_BOUNDS give each step's position. A step without one keeps its
/// default position (1 to 5, the reference order). Two steps in the same position are a configuration error.
/// </summary>
public static class StepOrder
{
    public const string Prefix = "STEP_";

    public static IReadOnlyList<RatingStep> Default { get; } = Enum.GetValues<RatingStep>();

    public static IReadOnlyList<RatingStep> For(IReadOnlyList<PremiumParameter> parameters)
    {
        var positions = Default.ToDictionary(step => step, step => (decimal)((int)step + 1));
        foreach (var parameter in parameters.Where(p => p.Code.StartsWith(Prefix, StringComparison.Ordinal)))
        {
            var name = parameter.Code[Prefix.Length..];
            if (!Enum.TryParse<RatingStep>(name, ignoreCase: true, out var step) || !string.Equals(step.ToString(), name, StringComparison.OrdinalIgnoreCase))
            {
                throw new RatingEngine.RatingException(RatingErrorCodes.ConfigurationIncomplete,
                    $"{parameter.Code} is not a rating step; the steps are {string.Join(", ", Default.Select(d => Prefix + d.ToString().ToUpperInvariant()))}.");
            }

            positions[step] = parameter.Value;
        }

        if (positions.Values.Distinct().Count() != positions.Count)
        {
            throw new RatingEngine.RatingException(RatingErrorCodes.ConfigurationIncomplete, "Two rating steps have the same position.");
        }

        return positions.OrderBy(p => p.Value).Select(p => p.Key).ToList();
    }
}
