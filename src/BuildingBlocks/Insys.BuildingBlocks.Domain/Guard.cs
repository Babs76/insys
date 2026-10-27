/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Domain
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
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Insys.BuildingBlocks.Domain;

/// <summary>
/// A guarded value that is missing or too long (BR-073). The value usually comes from a request a validator did not
/// cover, so the API answers it as a 422 field error on <see cref="Field"/>, never a 500. The message holds no value.
/// </summary>
public sealed partial class GuardException(string field, string message) : ArgumentException(message, field)
{
    /// <summary>The field, in the request's JSON naming, from the guarded expression ("command.Reason" is "reason").</summary>
    public string Field { get; } = field;

    /// <summary>What is wrong with the field, without its value.</summary>
    public string Problem { get; } = message;

    internal static string FieldOf(string? expression)
    {
        var text = (expression ?? "value").Trim();
        for (var unwrapped = true; unwrapped;)
        {
            text = TrailingCalls().Replace(text, "").TrimEnd('!', '?');
            unwrapped = GuardCall().Match(text) is { Success: true } call;
            if (unwrapped)
            {
                text = GuardCall().Match(text).Groups["inner"].Value.Split(',')[0].Trim();
            }
        }

        var name = text[(text.LastIndexOf('.') + 1)..];
        return name.Length == 0 || !char.IsLetter(name[0]) ? "value" : char.ToLowerInvariant(name[0]) + name[1..];
    }

    [GeneratedRegex(@"^Guard\.\w+\((?<inner>.*)\)$")]
    private static partial Regex GuardCall();

    [GeneratedRegex(@"(\??\.\w+\(\))+$")]
    private static partial Regex TrailingCalls();
}

/// <summary>
/// Guards against programming errors. Business rule failures return <see cref="Result"/> instead. A missing or too long
/// string is a <see cref="GuardException"/>, which the API answers as a 422 field error (BR-073).
/// </summary>
public static class Guard
{
    public static T NotNull<T>(T? value, [CallerArgumentExpression(nameof(value))] string? name = null)
        where T : class =>
        value ?? throw new ArgumentNullException(name);

    public static string NotNullOrWhiteSpace(string? value, [CallerArgumentExpression(nameof(value))] string? name = null) =>
        string.IsNullOrWhiteSpace(value) ? throw Missing(name) : value;

    public static string MaxLength(string value, int maxLength, [CallerArgumentExpression(nameof(value))] string? name = null) =>
        value.Length > maxLength ? throw TooLong(name, maxLength) : value;

    private static GuardException Missing(string? expression)
    {
        var field = GuardException.FieldOf(expression);
        return new GuardException(field, $"{field} is required.");
    }

    private static GuardException TooLong(string? expression, int maxLength)
    {
        var field = GuardException.FieldOf(expression);
        return new GuardException(field, $"{field} must be at most {maxLength} characters.");
    }

    public static Guid NotEmpty(Guid value, [CallerArgumentExpression(nameof(value))] string? name = null) =>
        value == Guid.Empty ? throw new ArgumentException("Value must not be empty.", name) : value;

    public static decimal NotNegative(decimal value, [CallerArgumentExpression(nameof(value))] string? name = null) =>
        value < 0 ? throw new ArgumentOutOfRangeException(name, value, "Value must not be negative.") : value;
}
