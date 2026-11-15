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

/// <summary>One fact sent to a model: a name INSYS chose and a value of an allowed kind.</summary>
public sealed record AssistFact(string Name, string Value);

/// <summary>
/// What a prompt may hold (D-15, addendum §5.4): structured fields only. A value is a code, an amount with its currency, a number, a yes or no, a date, a field's
/// name, or the error catalogue's own wording. No free text goes in: a value that isn't one of these kinds is left out and counted, never masked and sent.
/// Every amount carries its currency, so a stated currency in the output is checked against it (addendum §5.1).
/// </summary>
public sealed partial class AllowedFacts
{
    private readonly List<AssistFact> _facts = [];

    public IReadOnlyList<AssistFact> Items => _facts;

    /// <summary>Values left out because they weren't of an allowed kind (free text, or a name or value of the wrong form).</summary>
    public int Excluded { get; private set; }

    public AllowedFacts Code(string name, string? value) => value is not null && CodeForm().IsMatch(value) ? Add(name, value) : Exclude();

    public AllowedFacts Number(string name, decimal value) => Add(name, value.ToString("0.##########", CultureInfo.InvariantCulture));

    public AllowedFacts Amount(string name, decimal value, string currency) =>
        CurrencyForm().IsMatch(currency) ? Add(name, $"{currency} {value.ToString("0.00", CultureInfo.InvariantCulture)}") : Exclude();

    public AllowedFacts Flag(string name, bool value) => Add(name, value ? "yes" : "no");

    public AllowedFacts Date(string name, DateOnly value) => Add(name, value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    /// <summary>A field's name, from a refusal's field errors: only the name, never the message the screen shows (AIA-039, D-15).</summary>
    public AllowedFacts Field(string name, string field) => FieldForm().IsMatch(field) ? Add(name, field) : Exclude();

    /// <summary>The error catalogue's own wording (AIA-031): INSYS's text, written beside each code, never a record's or a user's.</summary>
    internal AllowedFacts Wording(string name, string text) => Add(name, text);

    /// <summary>The facts as the model receives them, one per line: what the verifier compares numbers against.</summary>
    public string Render() => string.Join("\n", _facts.Select(f => $"{f.Name}: {f.Value}"));

    private AllowedFacts Add(string name, string value)
    {
        if (!NameForm().IsMatch(name))
        {
            throw new ArgumentException($"A fact's name is INSYS's own, lower case: {name}.", nameof(name));
        }

        _facts.Add(new AssistFact(name, value));
        return this;
    }

    private AllowedFacts Exclude()
    {
        Excluded++;
        return this;
    }

    [GeneratedRegex(@"^[a-z][a-z0-9 _]{0,59}$")]
    private static partial Regex NameForm();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.:\-]{0,59}$")]
    private static partial Regex CodeForm();

    [GeneratedRegex(@"^[A-Z]{3}$")]
    private static partial Regex CurrencyForm();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_.\[\]]{0,79}$")]
    private static partial Regex FieldForm();
}
