/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Party
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

namespace Insys.Party.Domain.Parties;

/// <summary>
/// Whether a number has an identity document type's format. The pattern is tenant data, so it runs without backtracking: its time grows
/// with the number's length whatever the pattern is, and a stalled machine can't make a valid number fail. A pattern that needs
/// backtracking (a lookaround or a backreference) runs with a time limit, and a number it can't decide in time doesn't match.
/// </summary>
public static class IdentifierFormat
{
    private static readonly TimeSpan BacktrackingLimit = TimeSpan.FromSeconds(5);

    public static bool Matches(string number, string pattern)
    {
        var whole = $"^(?:{pattern})$";
        try
        {
            return Regex.IsMatch(number, whole, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);
        }
        catch (NotSupportedException)
        {
            try
            {
                return Regex.IsMatch(number, whole, RegexOptions.CultureInvariant, BacktrackingLimit);
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }
    }
}
