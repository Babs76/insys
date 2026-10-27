/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Application
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
using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Insys.BuildingBlocks.Application.Context;

/// <summary>
/// The business date: today as the tenant sees it, in the tenant's time zone (GAP-044, ADR-025). Inception, cover start,
/// event and loss dates, due dates, lapse, period close, static data in force and job run dates are judged by it.
/// Timestamps stay in UTC (<see cref="IClock.UtcNow"/>).
/// </summary>
public interface IBusinessDate
{
    /// <summary>Today in the current tenant's time zone.</summary>
    DateOnly Today { get; }

    /// <summary>Today in a given tenant's time zone, for work that spans tenants.</summary>
    DateOnly TodayFor(Guid tenantId);

    /// <summary>The business date of an instant in the current tenant's time zone.</summary>
    DateOnly DateOf(DateTimeOffset instant);

    /// <summary>The current tenant's time zone.</summary>
    TimeZoneInfo Zone { get; }
}

/// <summary>A tenant's IANA time zone (tenant.cfg_locale_defaults), read without awaiting; implemented by the Tenancy module.</summary>
public interface ITenantTimeZoneSource
{
    string? TimeZoneOf(Guid tenantId);
}

public sealed class BusinessDateOptions
{
    /// <summary>The time zone of a tenant with none, and of work with no tenant: the same default ADR-022 uses.</summary>
    public string DefaultTimeZone { get; set; } = "Africa/Johannesburg";

    /// <summary>How long a tenant's time zone is cached.</summary>
    public TimeSpan CacheFor { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Test only (Development and Testing; the host refuses it elsewhere): every tenant's business date is this day, so a test
    /// run judges "today" the same at any hour (Wave 1 batch 4). Instants and time zones are untouched; a host with a fixed
    /// clock leaves it unset to exercise the conversion itself.
    /// </summary>
    public DateOnly? TestDate { get; set; }
}

/// <summary>Tenants' time zones, cached for a minute; the default when a tenant has none or it is not a known zone.</summary>
public sealed class TenantTimeZones(IServiceProvider services, IClock clock, IOptions<BusinessDateOptions> options)
{
    private readonly ConcurrentDictionary<Guid, (TimeZoneInfo Zone, DateTimeOffset LoadedAt)> _zones = new();

    public TimeZoneInfo Default => Find(options.Value.DefaultTimeZone) ?? TimeZoneInfo.Utc;

    /// <summary>The test-only business date of every tenant (BusinessDateOptions.TestDate), or null.</summary>
    public DateOnly? TestDate => options.Value.TestDate;

    public TimeZoneInfo For(Guid? tenantId)
    {
        if (tenantId is not { } id || id == Guid.Empty)
        {
            return Default;
        }

        var now = clock.UtcNow;
        if (_zones.TryGetValue(id, out var cached) && now - cached.LoadedAt < options.Value.CacheFor)
        {
            return cached.Zone;
        }

        var source = services.GetService(typeof(ITenantTimeZoneSource)) as ITenantTimeZoneSource;
        var zone = Find(source?.TimeZoneOf(id)) ?? Default;
        _zones[id] = (zone, now);
        return zone;
    }

    public void Forget(Guid tenantId) => _zones.TryRemove(tenantId, out _);

    private static TimeZoneInfo? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }
}

/// <summary>
/// A business date set for the work running in this async flow, by the test-only job runner (Development and Testing
/// only, ADR-025): the daily jobs and everything they call judge that day as today.
/// </summary>
public static class BusinessDateOverride
{
    private static readonly AsyncLocal<DateOnly?> Current = new();

    public static DateOnly? Day => Current.Value;

    public static IDisposable Use(DateOnly day)
    {
        var previous = Current.Value;
        Current.Value = day;
        return new Restore(previous);
    }

    private sealed class Restore(DateOnly? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}

/// <summary>Tenants' business dates for work that spans tenants, such as the daily jobs. A singleton.</summary>
public sealed class TenantBusinessDates(TenantTimeZones zones, IClock clock)
{
    public DateOnly TodayFor(Guid tenantId) =>
        BusinessDateOverride.Day ?? zones.TestDate ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.UtcNow, zones.For(tenantId)).DateTime);

    /// <summary>
    /// The instant a business date starts in the tenant's time zone, so a cache of what was in force on one day ends with
    /// it (BR-040). Null when a test date fixes the business date, which then never changes with the clock.
    /// </summary>
    public DateTimeOffset? StartOf(Guid tenantId, DateOnly day)
    {
        if (BusinessDateOverride.Day is not null || zones.TestDate is not null)
        {
            return null;
        }

        var zone = zones.For(tenantId);
        var midnight = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        // A zone that skips midnight for daylight saving starts the day at its first valid time.
        while (zone.IsInvalidTime(midnight))
        {
            midnight = midnight.AddMinutes(15);
        }

        return new DateTimeOffset(midnight, zone.GetUtcOffset(midnight));
    }
}

/// <summary>The business date in the current scope's tenant.</summary>
public sealed class BusinessDate(ITenantContext tenant, TenantTimeZones zones, IClock clock) : IBusinessDate
{
    public TimeZoneInfo Zone => zones.For(tenant.TenantId);

    public DateOnly Today => BusinessDateOverride.Day ?? zones.TestDate ?? DateOf(clock.UtcNow);

    public DateOnly TodayFor(Guid tenantId) =>
        BusinessDateOverride.Day ?? zones.TestDate ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.UtcNow, zones.For(tenantId)).DateTime);

    public DateOnly DateOf(DateTimeOffset instant) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, Zone).DateTime);
}
