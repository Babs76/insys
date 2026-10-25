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
/// The fixed test clock for dates (Wave 1 batch 4, ADR-025): every test and every test host judge "today" as this one day,
/// the UTC date the test run started, whatever the hour. The hosts get it as BusinessDate:TestDate (test only), so a run
/// between 22:00 and 24:00 UTC, when Johannesburg is already on the next date, behaves as any other. A host given a fixed
/// clock of its own (BusinessDateTests, the 23:30 UTC boundary cases) leaves it unset to exercise the time-zone conversion.
/// </summary>
public static class TestClock
{
    public static DateOnly Today { get; } = DateOnly.FromDateTime(DateTime.UtcNow);

    public static string TodayText => Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The hosts' default time zone: Africa/Johannesburg, or INSYS_TEST_DEFAULT_TIME_ZONE (Pacific/Kiritimati, UTC+14, is
    /// already tomorrow for most of the UTC day), to show at any hour that no test depends on the hour it runs.
    /// </summary>
    public static string DefaultTimeZone { get; } =
        Environment.GetEnvironmentVariable("INSYS_TEST_DEFAULT_TIME_ZONE") is { Length: > 0 } zone ? zone : "Africa/Johannesburg";
}
