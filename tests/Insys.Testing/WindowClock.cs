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
/// The end-to-end hosts' clock (BR-040; fixed dates, Wave 3 batch 1): a fixed instant, advancing in real time from the
/// start of the run, so no run depends on the day it runs. INSYS_TEST_CLOCK names the instant (ISO 8601, UTC); without
/// it the clock starts at <see cref="Default"/>, 22:30 UTC, when Johannesburg is already on the next date, so every run
/// exercises the hours in which the business date and the UTC date differ. CI also runs the process flows on the dates
/// in <see cref="Dates"/>: the 28th, 30th and 31st of a month, 29 February in a leap year, and 23:30 UTC on a month end.
/// The hosts take no test date: they convert instants to the tenant's date as production does, and the tests expect that
/// date (<see cref="Today"/>).
/// </summary>
public static class WindowClock
{
    /// <summary>The default start: 22:30 UTC on 15 March 2027, the 16th in Johannesburg.</summary>
    public static readonly DateTimeOffset Default = new(2027, 3, 15, 22, 30, 0, TimeSpan.Zero);

    /// <summary>The fixed instants CI runs the billing, Life, Health and General flows on, by name (the CI job matrix).</summary>
    public static readonly IReadOnlyDictionary<string, DateTimeOffset> Dates = new Dictionary<string, DateTimeOffset>
    {
        ["28th"] = new(2027, 1, 28, 10, 0, 0, TimeSpan.Zero),
        ["30th"] = new(2027, 1, 30, 10, 0, 0, TimeSpan.Zero),
        ["31st"] = new(2027, 1, 31, 10, 0, 0, TimeSpan.Zero),
        ["leap-29-feb"] = new(2028, 2, 29, 10, 0, 0, TimeSpan.Zero),
        ["2330-utc-month-end"] = new(2027, 3, 31, 23, 30, 0, TimeSpan.Zero),
    };

    /// <summary>The instant the run's clock starts at.</summary>
    public static DateTimeOffset Start { get; }

    /// <summary>The clock: <see cref="Start"/> plus the real time elapsed since. Timers run in real time.</summary>
    public static TimeProvider Time { get; }

    /// <summary>The business date at <see cref="Start"/> in the hosts' default time zone.</summary>
    public static DateOnly Today { get; }

    static WindowClock()
    {
        var now = DateTimeOffset.UtcNow;
        Start = Environment.GetEnvironmentVariable("INSYS_TEST_CLOCK") is { Length: > 0 } fixedAt
            ? Dates.TryGetValue(fixedAt, out var named) ? named : DateTimeOffset.Parse(fixedAt, System.Globalization.CultureInfo.InvariantCulture)
            : Default;
        Time = new Shifted(Start - now);
        Today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(Start, TimeZoneInfo.FindSystemTimeZoneById(TestClock.DefaultTimeZone)).DateTime);
    }

    private sealed class Shifted(TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + offset;
    }
}
