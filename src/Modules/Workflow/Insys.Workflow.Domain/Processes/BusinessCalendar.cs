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
using Insys.BuildingBlocks.Domain;
using Insys.Workflow.Domain.Approvals;

namespace Insys.Workflow.Domain.Processes;

/// <summary>
/// A business calendar (workflow.ref_business_calendars) with its public holidays: SLA time runs only on working days,
/// within working hours, in the calendar's time zone (OQ-049). Hours "00:00–24:00" on every day is round the clock.
/// </summary>
public sealed class BusinessCalendar
{
    private const int MaxDays = 3660;
    private readonly HashSet<DayOfWeek> _workingDays;
    private readonly HashSet<DateOnly> _holidays;
    private readonly TimeSpan _open;
    private readonly TimeSpan _close;
    private readonly TimeZoneInfo _zone;

    private BusinessCalendar(string code, HashSet<DayOfWeek> workingDays, TimeSpan open, TimeSpan close, TimeZoneInfo zone, HashSet<DateOnly> holidays)
    {
        Code = code;
        _workingDays = workingDays;
        _open = open;
        _close = close;
        _zone = zone;
        _holidays = holidays;
    }

    public string Code { get; }

    /// <param name="workingDays">Day names such as "Mon" or "Monday".</param>
    /// <param name="hours">"08:00–17:00" (an en dash or a hyphen); "24:00" is the end of the day.</param>
    /// <param name="timeZone">An IANA time zone, such as Africa/Johannesburg.</param>
    public static Result<BusinessCalendar> Create(string code, IEnumerable<string> workingDays, string hours, string timeZone, IEnumerable<DateOnly> holidays)
    {
        var days = new HashSet<DayOfWeek>();
        foreach (var name in workingDays)
        {
            var day = Enum.GetValues<DayOfWeek>().FirstOrDefault(d => d.ToString().StartsWith(name.Trim(), StringComparison.OrdinalIgnoreCase) && name.Trim().Length >= 3, (DayOfWeek)(-1));
            if (day == (DayOfWeek)(-1))
            {
                return Invalid(code, $"'{name}' is not a day of the week.");
            }

            days.Add(day);
        }

        var parts = hours.Split(['–', '-', '—'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (days.Count == 0 || parts.Length != 2 || Time(parts[0]) is not { } open || Time(parts[1]) is not { } close || close <= open)
        {
            return Invalid(code, $"working days and hours '{hours}' do not describe any working time.");
        }

        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return Invalid(code, $"'{timeZone}' is not a known time zone.");
        }

        return new BusinessCalendar(code, days, open, close, zone, holidays.ToHashSet());
    }

    /// <summary>The moment <paramref name="duration"/> of working time after <paramref name="start"/>.</summary>
    public DateTimeOffset AddWorkingTime(DateTimeOffset start, TimeSpan duration)
    {
        var local = TimeZoneInfo.ConvertTime(start, _zone);
        var date = DateOnly.FromDateTime(local.DateTime);
        var time = local.TimeOfDay;
        var remaining = duration;
        for (var i = 0; i < MaxDays; i++)
        {
            if (IsWorkingDay(date) && time < _close)
            {
                var from = time < _open ? _open : time;
                var available = _close - from;
                if (remaining <= available)
                {
                    return At(date, from + remaining);
                }

                remaining -= available;
            }

            date = date.AddDays(1);
            time = TimeSpan.Zero;
        }

        throw new InvalidOperationException($"Business calendar {Code} has no working time within {MaxDays} days.");
    }

    /// <summary>
    /// An ISO 8601 SLA duration on this calendar: whole days count working days (the same working time of day, so
    /// "P2D" is two working days), and the time part counts working hours.
    /// </summary>
    public DateTimeOffset Add(DateTimeOffset start, int days, TimeSpan time)
    {
        var due = start;
        if (days > 0)
        {
            var dayLength = _close - _open;
            due = AddWorkingTime(start, TimeSpan.FromTicks(dayLength.Ticks * days));
        }

        return time > TimeSpan.Zero ? AddWorkingTime(due, time) : due;
    }

    private bool IsWorkingDay(DateOnly date) => _workingDays.Contains(date.DayOfWeek) && !_holidays.Contains(date);

    private DateTimeOffset At(DateOnly date, TimeSpan time)
    {
        var local = date.ToDateTime(TimeOnly.MinValue).Add(time);
        return new DateTimeOffset(local, _zone.GetUtcOffset(local)).ToUniversalTime();
    }

    private static TimeSpan? Time(string text) =>
        text == "24:00" ? TimeSpan.FromHours(24)
        : TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t.ToTimeSpan() : null;

    private static Error Invalid(string code, string message) => Error.Validation(WorkflowErrorCodes.DefinitionInvalid, $"Business calendar {code}: {message}");
}
