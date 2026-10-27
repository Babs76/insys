/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Testing
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
namespace Insys.Testing;

/// <summary>
/// The end-to-end hosts' clock (BR-040): 22:30 UTC on the latest day that time has passed, advancing in real time from the
/// start of the run. Between 22:00 and 24:00 UTC Johannesburg is already on the next date, so every run exercises the hours
/// in which the business date and the UTC date differ, whatever time CI starts. The hosts take no test date: they convert
/// instants to the tenant's date as production does, and the tests expect that date (<see cref="Today"/>).
/// A process suite lasts minutes; the window leaves 90 minutes before midnight UTC.
/// </summary>
public static class WindowClock
{
    private static readonly TimeOnly At = new(22, 30);

    /// <summary>The instant the run's clock starts at.</summary>
    public static DateTimeOffset Start { get; }

    /// <summary>The clock: <see cref="Start"/> plus the real time elapsed since. Timers run in real time.</summary>
    public static TimeProvider Time { get; }

    /// <summary>The business date at <see cref="Start"/> in the hosts' default time zone: the UTC date plus one in Johannesburg.</summary>
    public static DateOnly Today { get; }

    static WindowClock()
    {
        var now = DateTimeOffset.UtcNow;
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var start = new DateTimeOffset(today.ToDateTime(At), TimeSpan.Zero);
        Start = start <= now ? start : start.AddDays(-1);
        Time = new Shifted(Start - now);
        Today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(Start, TimeZoneInfo.FindSystemTimeZoneById(TestClock.DefaultTimeZone)).DateTime);
    }

    private sealed class Shifted(TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + offset;
    }
}
