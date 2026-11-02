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
using System.Text.RegularExpressions;

namespace Insys.Assist.Application;

/// <summary>What was minimised, and how many of each were masked. The text is the payload actually sent, which the run stores (AIA-051).</summary>
public sealed record MinimisedPayload(string Text, IReadOnlyDictionary<string, int> Masked);

/// <summary>
/// AIA-051: before any call, removes names, identifiers, contact and bank details, and policy, claim and member numbers. The caller names what it knows of the record
/// (a party's name, a policy number); patterns catch what looks like the rest, so an unnamed identifier in free text still doesn't leave. Amounts, rates and dates stay,
/// as the explanation needs them, and the verifier compares against this text.
/// </summary>
public static partial class Minimiser
{
    /// <param name="known">The record's names and numbers, by kind, such as NAME or POLICY_NUMBER.</param>
    public static MinimisedPayload Minimise(string text, IReadOnlyDictionary<string, IReadOnlyCollection<string>>? known = null)
    {
        var masked = new Dictionary<string, int>(StringComparer.Ordinal);
        string Mask(string kind, string input, Regex pattern) => pattern.Replace(input, _ =>
        {
            masked[kind] = masked.GetValueOrDefault(kind) + 1;
            return $"[{kind}]";
        });

        // Longest first, so a full name goes before a part of it. A name's own words go too, so "Lee, Ann" and "A. Lee" don't leave.
        var values = (known ?? new Dictionary<string, IReadOnlyCollection<string>>()).SelectMany(k => k.Value.SelectMany(v =>
                k.Key == "NAME" ? [v, .. v.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(w => w.Length > 1)] : new[] { v }).Select(v => (k.Key, v)))
            .Where(k => !string.IsNullOrWhiteSpace(k.v)).OrderByDescending(k => k.v.Length);
        foreach (var (kind, value) in values)
        {
            text = Mask(kind, text, new Regex($@"(?<![\p{{L}}\p{{Nd}}]){Regex.Escape(value.Trim())}(?![\p{{L}}\p{{Nd}}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));
        }

        text = Mask("EMAIL", text, Email());
        text = Mask("EMAIL", text, SpokenEmail());
        text = Mask("IBAN", text, Iban());
        text = Mask("REFERENCE", text, Reference());
        text = Mask("PHONE", text, Phone());
        text = Mask("ID_NUMBER", text, GroupedDigits());
        text = Mask("ID_NUMBER", text, LongDigits());
        return new MinimisedPayload(text, masked);
    }

    /// <summary>
    /// Free text can name anyone, and no pattern finds a name it wasn't told: every capitalised word goes as well, at the cost of some of the text's sense (AIA-051). A name written in lower case can still pass, so text from people isn't to be sent to a model without review (addendum §2, D-3).
    /// </summary>
    public static MinimisedPayload MinimiseFreeText(string text, IReadOnlyDictionary<string, IReadOnlyCollection<string>>? known = null)
    {
        var payload = Minimise(text, known);
        var masked = new Dictionary<string, int>(payload.Masked, StringComparer.Ordinal);
        var words = CapitalisedWord().Replace(payload.Text, _ =>
        {
            masked["NAME"] = masked.GetValueOrDefault("NAME") + 1;
            return "[NAME]";
        });
        return new MinimisedPayload(words, masked);
    }

    [GeneratedRegex(@"(?<![\[\p{L}])\p{Lu}[\p{L}'-]*(?![\]\p{L}])")]
    private static partial Regex CapitalisedWord();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+")]
    private static partial Regex Email();

    [GeneratedRegex(@"\b[A-Za-z0-9._%+-]+ at [A-Za-z0-9-]+( dot [A-Za-z]{2,})+\b", RegexOptions.IgnoreCase)]
    private static partial Regex SpokenEmail();

    [GeneratedRegex(@"\b[A-Za-z]{2}[0-9]{2}(?:[ ]?[A-Za-z0-9]){11,30}\b")]
    private static partial Regex Iban();

    /// <summary>A number with letters or slashes, as policy, claim and member numbers are: GC-20261101-0007, mbr123456, POL/2026/00123, POL 123456, P123456, 12345/01.</summary>
    [GeneratedRegex(@"\b(?:(?:POL|CLM|MBR|GC|LC|HC|POLICY|CLAIM|MEMBER)[ /-]?[0-9][A-Z0-9/-]{2,}|(?=[A-Za-z0-9/-]*[0-9])(?![A-Z]{3}[0-9])[A-Za-z]{1,}[-/]?[0-9][A-Za-z0-9/-]{3,}|[0-9]{2,}/[0-9/]{2,})\b", RegexOptions.IgnoreCase)]
    private static partial Regex Reference();

    /// <summary>International, or national with a leading 0, however it is grouped; digits and separators only, as most African national numbers are 10 digits starting with 0.</summary>
    [GeneratedRegex(@"(?<![0-9])(?:\+[0-9]{1,3}[ .()-]*(?:[0-9][ .()-]*){7,12}[0-9]|\(?\b0[0-9]{1,3}\)?[ .-]?[0-9]{2,4}[ .-]?[0-9]{2,4}(?:[ .-]?[0-9]{2})?\b)(?![0-9])")]
    private static partial Regex Phone();

    /// <summary>Digits in groups that aren't thousands: 800101 5009 087, 80-01-01-5009-087, 4111 1111 1111 1111, 6201 2345 678, 123-45-6789, 77 123 45 67.</summary>
    [GeneratedRegex(@"(?<![0-9.,])(?=(?:[0-9][ -]?){8,})(?![0-9]{1,3}(?: [0-9]{3})+(?![ -]?[0-9]))(?![0-9]{4}-[0-9]{2}-[0-9]{2}(?![0-9-]))[0-9]+(?:[ -][0-9]+)+(?![0-9])")]
    private static partial Regex GroupedDigits();

    /// <summary>Eight or more digits with no separators: an identity or bank account number, not an amount as INSYS writes one, unless it runs on as an amount's decimals.</summary>
    [GeneratedRegex(@"(?<![0-9])[0-9]{8,}(?:\.[0-9]+)?")]
    private static partial Regex LongDigits();
}
