/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Assist
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
using System.Text.RegularExpressions;

namespace Insys.Assist.Application;

/// <summary>What the verifier found: the numbers in the output the source doesn't hold. Empty means it passes (AIA-033).</summary>
public sealed record NumberCheck(IReadOnlyList<string> Rejected)
{
    public bool Passes => Rejected.Count == 0;
}

/// <summary>
/// The deterministic number check of AIA-033 and AIA-033a, by the rules in addendum §5.1. Every number in the output must be in the source once normalised: currency signs
/// and codes, thousands separators and decimal places don't change a number; percentages and their fractions are the same; dates compare as dates. A stated currency must
/// match. Percentages never match a bare amount. English only (D-4): a comma before exactly three digits is a separator, and any other comma isn't read as part of a number.
/// </summary>
public static partial class NumberVerifier
{
    private static readonly string[] Months = ["january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december"];

    private static readonly Dictionary<string, string> Signs = new(StringComparer.Ordinal) { ["R"] = "ZAR", ["$"] = "USD", ["€"] = "EUR", ["£"] = "GBP", ["₦"] = "NGN" };

    private static readonly HashSet<string> Currencies = new(["ZAR", "NGN", "USD", "EUR", "GBP", "RWF", "XOF", "XAF", "CDF", "KES", "GHS"], StringComparer.Ordinal);

    private enum Kind
    {
        Plain,
        Percent,
        Date,
    }

    /// <param name="Value">A percent as its fraction; a date as its yyyyMMdd.</param>
    private sealed record Number(Kind Kind, decimal Value, string? Currency, string Text);

    public static NumberCheck Verify(string output, string source)
    {
        var inSource = Read(source);
        var rejected = Read(output).Where(n => !inSource.Any(s => Same(n, s))).Select(n => n.Text).ToList();
        return new NumberCheck(rejected);
    }

    private static bool Same(Number output, Number source)
    {
        // A year in a source date may be written alone: "in 2026" from "1 November 2026".
        if (output.Kind == Kind.Plain && source.Kind == Kind.Date)
        {
            return output.Currency is null && output.Value == Math.Floor(source.Value / 10000);
        }

        if (output.Kind == Kind.Date || source.Kind == Kind.Date)
        {
            return output.Kind == source.Kind && output.Value == source.Value;
        }

        // A percentage matches a percentage or its fraction, written either way; never an amount of the percentage's own digits.
        if (output.Kind == Kind.Percent || source.Kind == Kind.Percent)
        {
            return output.Value == source.Value && output.Currency is null && source.Currency is null;
        }

        return output.Value == source.Value && (output.Currency is null || source.Currency is null || output.Currency == source.Currency);
    }

    private static List<Number> Read(string text)
    {
        var found = new List<Number>();
        foreach (Match date in IsoDate().Matches(text))
        {
            found.Add(new Number(Kind.Date, decimal.Parse(date.Value.Replace("-", "", StringComparison.Ordinal), CultureInfo.InvariantCulture), null, date.Value));
        }

        var rest = IsoDate().Replace(text, " ");
        foreach (Match date in LongDate().Matches(rest))
        {
            var month = Array.FindIndex(Months, m => m.StartsWith(date.Groups["month"].Value.ToLowerInvariant(), StringComparison.Ordinal)) + 1;
            found.Add(new Number(Kind.Date, (int.Parse(date.Groups["year"].Value, CultureInfo.InvariantCulture) * 10000) + (month * 100) + int.Parse(date.Groups["day"].Value, CultureInfo.InvariantCulture),
                null, date.Value));
        }

        rest = LongDate().Replace(rest, " ");
        foreach (Match number in Amount().Matches(rest))
        {
            // Three capitals run into digits are a code, such as AIA033, unless they are a currency.
            if (number.Groups["before"].Value is { Length: 3 } letters && !Currencies.Contains(letters) && !number.Value.Contains(letters + " ", StringComparison.Ordinal))
            {
                continue;
            }

            var digits = number.Groups["number"].Value.Replace(",", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
            var value = decimal.Parse(digits, CultureInfo.InvariantCulture) * (number.Groups["minus"].Success ? -1 : 1);
            var currency = number.Groups["before"].Success ? number.Groups["before"].Value : number.Groups["after"].Success ? number.Groups["after"].Value : null;
            found.Add(number.Groups["percent"].Success
                ? new Number(Kind.Percent, value / 100, null, number.Value.Trim())
                : new Number(Kind.Plain, value, currency is null ? null : Signs.GetValueOrDefault(currency, currency), number.Value.Trim()));
        }

        return found;
    }

    [GeneratedRegex(@"(?<![0-9-])[0-9]{4}-[0-9]{2}-[0-9]{2}(?![0-9-])")]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"\b(?<day>[0-9]{1,2}) (?<month>Jan(uary)?|Feb(ruary)?|Mar(ch)?|Apr(il)?|May|June?|July?|Aug(ust)?|Sep(tember)?|Oct(ober)?|Nov(ember)?|Dec(ember)?) (?<year>[0-9]{4})\b", RegexOptions.IgnoreCase)]
    private static partial Regex LongDate();

    /// <summary>
    /// A minus only when nothing runs into it, so a hyphen isn't one. Grouped by a comma or a space before exactly three digits. A digit run into a letter is a code, not a number, unless the letters are a currency sign or code.
    /// </summary>
    [GeneratedRegex(@"(?<![0-9A-Za-z.,])(?<minus>-(?=[R$€£₦A-Z0-9]))?(?:(?<before>R|\$|€|£|₦|[A-Z]{3}) ?)?(?<number>[0-9]{1,3}(?:[, ][0-9]{3})+(?![0-9])(?:\.[0-9]+)?|[0-9]+(?:\.[0-9]+)?)(?![A-Za-z0-9])(?:(?<percent> ?%| percent\b)|(?: (?<after>[A-Z]{3})\b))?")]
    private static partial Regex Amount();
}
