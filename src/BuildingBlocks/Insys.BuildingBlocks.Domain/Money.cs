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
using System.Globalization;

namespace Insys.BuildingBlocks.Domain;

/// <summary>An ISO 4217 alphabetic currency code.</summary>
public readonly record struct CurrencyCode
{
    public CurrencyCode(string code)
    {
        if (code is not { Length: 3 } || !code.All(char.IsAsciiLetterUpper))
        {
            throw new ArgumentException("A currency code is three upper-case letters (ISO 4217).", nameof(code));
        }

        Code = code;
    }

    public string Code { get; }

    public override string ToString() => Code;
}

/// <summary>
/// How to round an amount. Decimal places and mode come from static data (refdata currency and rounding rules);
/// callers must not hard-code them. TODO(spec): the rounding rule source is fixed in Phase 1 with the static data resolver.
/// </summary>
public sealed record RoundingRule(int DecimalPlaces, MidpointRounding Mode)
{
    public static RoundingRule Of(int decimalPlaces, MidpointRounding mode = MidpointRounding.ToEven) =>
        decimalPlaces is < 0 or > 10 ? throw new ArgumentOutOfRangeException(nameof(decimalPlaces)) : new(decimalPlaces, mode);
}

/// <summary>An amount in one currency (DBA-012). Never use double or float for money.</summary>
public readonly record struct Money(decimal Amount, CurrencyCode Currency)
{
    public static Money Zero(CurrencyCode currency) => new(0m, currency);

    public static Money Of(decimal amount, string currency) => new(amount, new CurrencyCode(currency));

    public bool IsZero => Amount == 0m;

    public bool IsNegative => Amount < 0m;

    public Result<Money> Add(Money other) =>
        other.Currency != Currency ? MismatchError(other) : new Money(Amount + other.Amount, Currency);

    public Result<Money> Subtract(Money other) =>
        other.Currency != Currency ? MismatchError(other) : new Money(Amount - other.Amount, Currency);

    public Money Negate() => this with { Amount = -Amount };

    public Money Multiply(decimal factor) => this with { Amount = Amount * factor };

    public Money Round(RoundingRule rule) => this with { Amount = Math.Round(Amount, rule.DecimalPlaces, rule.Mode) };

    public override string ToString() => $"{Amount.ToString(CultureInfo.InvariantCulture)} {Currency}";

    private Error MismatchError(Money other) =>
        Error.Validation(ErrorCodes.CurrencyMismatch, $"Cannot combine {Currency} with {other.Currency}.");
}
