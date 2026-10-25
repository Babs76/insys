/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Workflow
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

namespace Insys.Workflow.Domain.Cases;

/// <summary>ISO 8601 durations (P5Y, P10D, PT4H) added in calendar time: for CALENDAR timelines and retention periods.</summary>
public static partial class IsoDurations
{
    [GeneratedRegex(@"^P(?:(\d+)Y)?(?:(\d+)M)?(?:(\d+)W)?(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static bool IsValid(string? iso) => iso is { Length: > 1 } && iso != "PT" && Pattern().IsMatch(iso);

    /// <summary>The instant the duration after <paramref name="start"/>, on the calendar (years and months as calendar years and months).</summary>
    public static DateTimeOffset Add(DateTimeOffset start, string iso)
    {
        var p = Parse(iso);
        return start.AddYears(p[0]).AddMonths(p[1]).AddDays((p[2] * 7) + p[3]).AddHours(p[4]).AddMinutes(p[5]).AddSeconds(p[6]);
    }

    /// <summary>The date the duration's whole days, months and years after <paramref name="day"/>.</summary>
    public static DateOnly Add(DateOnly day, string iso)
    {
        var p = Parse(iso);
        return day.AddYears(p[0]).AddMonths(p[1]).AddDays((p[2] * 7) + p[3]);
    }

    private static int[] Parse(string iso)
    {
        var match = Pattern().Match(iso ?? "");
        if (!match.Success || !IsValid(iso))
        {
            throw new FormatException("Not an ISO 8601 duration.");
        }

        return Enumerable.Range(1, 7).Select(i => match.Groups[i].Success ? int.Parse(match.Groups[i].Value, CultureInfo.InvariantCulture) : 0).ToArray();
    }
}
