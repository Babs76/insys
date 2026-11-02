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

        // Longest first, so a full name goes before a part of it.
        foreach (var (kind, value) in (known ?? new Dictionary<string, IReadOnlyCollection<string>>()).SelectMany(k => k.Value.Select(v => (k.Key, v)))
                     .Where(k => !string.IsNullOrWhiteSpace(k.v)).OrderByDescending(k => k.v.Length))
        {
            text = Mask(kind, text, new Regex(Regex.Escape(value.Trim()), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));
        }

        text = Mask("EMAIL", text, Email());
        text = Mask("IBAN", text, Iban());
        text = Mask("REFERENCE", text, Reference());
        text = Mask("PHONE", text, Phone());
        text = Mask("ID_NUMBER", text, LongDigits());
        return new MinimisedPayload(text, masked);
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)+")]
    private static partial Regex Email();

    [GeneratedRegex(@"\b[A-Z]{2}[0-9]{2}[A-Z0-9]{11,30}\b")]
    private static partial Regex Iban();

    /// <summary>A number with letters, as policy, claim and member numbers are: GC-20261101-0007, MBR123456.</summary>
    [GeneratedRegex(@"\b(?=[A-Z0-9-]*[0-9])[A-Z]{2,}-?[0-9][A-Z0-9-]{3,}\b")]
    private static partial Regex Reference();

    /// <summary>An international number, or a national one that starts with 0.</summary>
    [GeneratedRegex(@"(?<![0-9.,])(\+[0-9]{1,3}[ -]?([0-9][ -]?){7,12}[0-9]|\b0[0-9]{2}[ -]?[0-9]{3}[ -]?[0-9]{4})\b")]
    private static partial Regex Phone();

    /// <summary>Nine or more digits with no separators: an identity or bank account number, not an amount as INSYS writes one.</summary>
    [GeneratedRegex(@"(?<![0-9.,])[0-9]{9,}(?![0-9]*[.,][0-9])")]
    private static partial Regex LongDigits();
}
