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

    private static readonly Dictionary<string, string> Signs = new(StringComparer.Ordinal) { ["R"] = "ZAR", ["$"] = "USD", ["€"] = "EUR", ["£"] = "GBP", ["₦"] = "NGN", ["₵"] = "GHS", ["¥"] = "JPY", ["US$"] = "USD", ["A$"] = "AUD",
        ["KSH"] = "KES", ["RS"] = "INR", ["FRW"] = "RWF", ["FCFA"] = "XOF", ["CFA"] = "XOF", ["DOLLAR"] = "USD", ["DOLLARS"] = "USD", ["RAND"] = "ZAR", ["RANDS"] = "ZAR", ["NAIRA"] = "NGN", ["EURO"] = "EUR", ["EUROS"] = "EUR", ["POUND"] = "GBP", ["POUNDS"] = "GBP", ["CEDI"] = "GHS", ["CEDIS"] = "GHS", ["SHILLING"] = "KES", ["SHILLINGS"] = "KES", ["FRANC"] = "XOF", ["FRANCS"] = "XOF" };

    private static readonly HashSet<string> Currencies = new(["ZAR", "NGN", "USD", "EUR", "GBP", "RWF", "XOF", "XAF", "CDF", "KES", "GHS"], StringComparer.Ordinal);

    private enum Kind
    {
        Plain,
        Percent,
        Date,
        MonthYear,
    }

    /// <param name="Value">A percent as its fraction; a date as its yyyyMMdd.</param>
    private sealed record Number(Kind Kind, decimal Value, string? Currency, string Text);

    public static NumberCheck Verify(string output, string source)
    {
        var (inSource, sourceRest) = Read(source);
        var sourceTokens = Tokens().Matches(source).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);
        var (inOutput, rest) = Read(output);
        var rejected = inOutput.Where(n => !inSource.Any(s => Same(n, s))).Select(n => n.Text).ToList();

        // Fails closed: any digit left unread, and any number or magnitude word, must stand word for word in the source (addendum §5.1 is about what is read; nothing else may slip past it).
        rejected.AddRange(Tokens().Matches(rest).Select(m => m.Value).Where(t => t.Any(char.IsDigit) && !sourceTokens.Contains(t)));
        rejected.AddRange(Words().Matches(rest).Select(m => m.Value).Where(w => !Words().Matches(sourceRest).Any(s => s.Value.Equals(w, StringComparison.OrdinalIgnoreCase))));
        return new NumberCheck(rejected);
    }

    private static bool Same(Number output, Number source)
    {
        if (output.Kind == Kind.MonthYear)
        {
            return source.Kind is Kind.Date or Kind.MonthYear && output.Value == Math.Floor(source.Kind == Kind.Date ? source.Value / 100 : source.Value);
        }

        // A year in a source date may be written alone: "in 2026" from "1 November 2026".
        if (output.Kind == Kind.Plain && source.Kind is Kind.Date or Kind.MonthYear)
        {
            return output.Currency is null && output.Value == Math.Floor(source.Value / (source.Kind == Kind.Date ? 10000 : 100));
        }

        if (output.Kind == Kind.Date || source.Kind is Kind.Date or Kind.MonthYear)
        {
            return output.Kind == source.Kind && output.Value == source.Value;
        }

        // A percentage matches a percentage, or its fraction written as one: never an amount, so 100% doesn't match a 1.
        if (output.Kind != source.Kind)
        {
            var plain = output.Kind == Kind.Plain ? output : source;
            return output.Value == source.Value && plain.Currency is null && Math.Abs(plain.Value) is > 0 and < 1;
        }

        return output.Value == source.Value && (output.Currency is null || source.Currency is null || output.Currency == source.Currency);
    }

    private static (List<Number> Numbers, string Unread) Read(string text)
    {
        var found = new List<Number>();
        text = Minus().Replace(text.Replace('\u2212', '-').Replace('\u2013', '-'), "-");
        var rest = IsoDate().Replace(text, m =>
        {
            found.Add(new Number(Kind.Date, decimal.Parse(m.Value.Replace("-", "", StringComparison.Ordinal), CultureInfo.InvariantCulture), null, m.Value));
            return " ";
        });
        rest = LongDate().Replace(rest, m =>
        {
            var month = Array.FindIndex(Months, n => n.StartsWith(m.Groups["month"].Value.ToLowerInvariant(), StringComparison.Ordinal)) + 1;
            var year = int.Parse(m.Groups["year"].Value, CultureInfo.InvariantCulture);
            found.Add(m.Groups["day"].Success
                ? new Number(Kind.Date, (year * 10000) + (month * 100) + int.Parse(m.Groups["day"].Value, CultureInfo.InvariantCulture), null, m.Value)
                : new Number(Kind.MonthYear, (year * 100) + month, null, m.Value));
            return " ";
        });
        rest = Amount().Replace(rest, m =>
        {
            var digits = m.Groups["number"].Value.Replace(",", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
            var before = m.Groups["before"].Success ? m.Groups["before"].Value : null;

            // Too long to read: left in the text, so the unread rule rejects it rather than the run failing and going unmetered.
            if ((before is { Length: 3 } && !Stated(before, out _) && !m.Value.Contains(before + " ", StringComparison.Ordinal))
                || !decimal.TryParse(digits, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
            {
                return m.Value;
            }

            value *= m.Groups["minus"].Success || m.Groups["minus2"].Success ? -1 : 1;
            var stated = before ?? (m.Groups["after"].Success ? m.Groups["after"].Value : null);
            found.Add(m.Groups["percent"].Success
                ? new Number(Kind.Percent, value / 100, null, m.Value.Trim())
                : new Number(Kind.Plain, value, stated is null ? null : Stated(stated, out var code) ? code : stated.ToUpperInvariant(), m.Value.Trim()));
            return " ";
        });
        return (found, rest);
    }

    private static bool Stated(string written, out string code)
    {
        var known = Signs.TryGetValue(written, out var sign) || Signs.TryGetValue(written.ToUpperInvariant(), out sign);
        code = sign ?? written.ToUpperInvariant();
        return known || Currencies.Contains(code);
    }

    [GeneratedRegex(@"(?<![0-9-])[0-9]{4}-[0-9]{2}-[0-9]{2}(?![0-9-])")]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"\b(?:(?<day>[0-9]{1,2}) )?(?<month>Jan(uary)?|Feb(ruary)?|Mar(ch)?|Apr(il)?|May|June?|July?|Aug(ust)?|Sep(tember)?|Oct(ober)?|Nov(ember)?|Dec(ember)?) (?<year>[0-9]{4})\b", RegexOptions.IgnoreCase)]
    private static partial Regex LongDate();

    /// <summary>"minus 50" and "- 50" are the same as -50.</summary>
    [GeneratedRegex(@"(?<![0-9A-Za-z.,] ?)(?:\bminus |- )(?=[R$€£₦₵¥0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex Minus();

    /// <summary>A minus only when nothing runs into it, so a hyphen isn't one. Grouped by a comma or a space before exactly three digits.</summary>
    [GeneratedRegex(@"(?<![0-9A-Za-z.,\p{Nd}])(?<minus>-(?=[R$€£₦₵¥A-Za-z0-9]))?(?:(?<before>US\$|A\$|KSh|Ksh|Rs|Frw|FCFA|CFA|R|\$|€|£|₦|₵|¥|[A-Z]{3}|usd|zar|ngn|rwf|kes|ghs|eur|gbp|xof|xaf|cdf) ?)?(?<minus2>-)?(?<number>[0-9]{1,3}(?:[, ][0-9]{3})+(?![0-9])(?:\.[0-9]+)?|[0-9]+(?:\.[0-9]+)?)(?![\p{L}\p{Nd}])(?![.,][0-9])(?:(?<percent> ?%| percent\b)|(?: (?<after>[A-Z]{3}|(?i:dollars?|rands?|naira|francs?|shillings?|cedis?|euros?|pounds?|fcfa|usd|zar|ngn|rwf|kes|ghs|eur|gbp))\b))?")]
    private static partial Regex Amount();

    [GeneratedRegex(@"[^\s]+")]
    private static partial Regex Tokens();

    /// <summary>"One" is ordinary English; every other number word and magnitude says a figure the verifier can't compare.</summary>
    [GeneratedRegex(@"\b(two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|sixteen|seventeen|eighteen|nineteen|twenty|thirty|forty|fifty|sixty|seventy|eighty|ninety|hundred|thousand|million|billion|trillion|half|quarter|double|twice|triple)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Words();
}
