/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Communications
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

namespace Insys.Communications.Infrastructure.Channels;

/// <summary>The rules the stub channels check a message against before "sending" it.</summary>
internal static partial class StubChannelRules
{
    /// <summary>Six concatenated GSM-7 segments of 153 characters.</summary>
    public const int SmsBodyMaxLength = 918;

    public const int PushBodyMaxLength = 1_000;

    public const int EmailAddressMaxLength = 254;

    public static bool IsE164(string? address) => address is not null && E164().IsMatch(address);

    public static bool IsPlausibleEmail(string? address) =>
        address is not null && address.Length <= EmailAddressMaxLength && Email().IsMatch(address);

    [GeneratedRegex(@"^\+[1-9][0-9]{7,14}$", RegexOptions.CultureInvariant)]
    private static partial Regex E164();

    /// <summary>One @, no whitespace, and a dot in the domain that neither starts nor ends it.</summary>
    [GeneratedRegex(@"^[^@\s]+@[^@\s.]+(\.[^@\s.]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex Email();
}
