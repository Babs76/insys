/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Billing
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

namespace Insys.Billing.Infrastructure;

/// <summary>
/// Whether the arrears ageing buckets cover every day overdue from day 1 to the last action (request 46, onboarding
/// readiness COLLECTION_METHODS): no gaps or overlaps, and the last bucket's action is LAPSE or CANCEL.
/// </summary>
public static class AgeingCoverage
{
    /// <param name="buckets">"daysFrom:daysTo:action", ordered by days from.</param>
    public static IReadOnlyList<string> Gaps(IReadOnlyList<string> buckets)
    {
        if (buckets.Count == 0)
        {
            return ["billing.cfg_arrears_ageing_buckets: no bucket in force"];
        }

        var parsed = buckets.Select(b => b.Split(':')).Select(p => (From: int.Parse(p[0], CultureInfo.InvariantCulture), To: int.Parse(p[1], CultureInfo.InvariantCulture),
            Action: p[2])).ToList();
        var gaps = new List<string>();
        if (parsed[0].From != 1)
        {
            gaps.Add($"ageing buckets start at day {parsed[0].From}, not day 1");
        }

        for (var i = 1; i < parsed.Count; i++)
        {
            if (parsed[i].From > parsed[i - 1].To + 1)
            {
                gaps.Add($"ageing buckets leave days {parsed[i - 1].To + 1} to {parsed[i].From - 1} uncovered");
            }
            else if (parsed[i].From <= parsed[i - 1].To)
            {
                gaps.Add($"ageing buckets overlap from day {parsed[i].From}");
            }
        }

        if (parsed[^1].Action is not ("LAPSE" or "CANCEL"))
        {
            gaps.Add($"the last ageing bucket's action is {parsed[^1].Action}, not LAPSE or CANCEL");
        }

        return gaps;
    }
}
