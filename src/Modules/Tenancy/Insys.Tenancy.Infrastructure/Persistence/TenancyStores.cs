/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Tenancy
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
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Localisation;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Contracts.StaticData;
using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.Tenancy.Infrastructure.StaticData;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Tenancy.Application.Tenants;
using Insys.Tenancy.Contracts;
using Insys.Tenancy.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.Tenancy.Infrastructure.Persistence;

internal sealed class TenantRepository(TenancyDbContext db) : ITenantRepository
{
    public Task<Tenant?> Get(Guid tenantId, CancellationToken cancellationToken) =>
        db.Tenants.Include("_onboardingCases").Include(t => t.StatusChanges).SingleOrDefaultAsync(t => t.Id == tenantId, cancellationToken);

    public Task<bool> CodeExists(string tenantCode, CancellationToken cancellationToken) =>
        db.Tenants.AnyAsync(t => t.TenantCode == tenantCode, cancellationToken);

    public void Add(Tenant tenant) => db.Tenants.Add(tenant);
}

internal sealed class TenantQueries(TenancyDbContext db) : ITenantQueries
{
    public async Task<IReadOnlyList<TenantSummary>> List(string? status, Guid? afterId, int limit, CancellationToken cancellationToken)
    {
        var query = db.Tenants.AsNoTracking();
        if (status is not null && Enum.TryParse<TenantStatus>(status, ignoreCase: true, out var parsed))
        {
            query = query.Where(t => t.Status == parsed);
        }

        if (afterId is not null)
        {
            query = query.Where(t => t.Id.CompareTo(afterId.Value) > 0);
        }

        var rows = await query.OrderBy(t => t.Id).Take(limit).ToListAsync(cancellationToken);
        return rows.Select(t => new TenantSummary(t.Id, t.TenantCode, t.LegalName, t.IsolationProfile.ToString(), t.Status, t.RowVersion, t.TimeZone)).ToList();
    }

    public async Task<TenantDetail?> Get(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants.AsNoTracking().Include("_onboardingCases").Include(t => t.StatusChanges)
            .SingleOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        return tenant is null
            ? null
            : new TenantDetail(tenant.Id, tenant.TenantCode, tenant.LegalName, tenant.IsolationProfile.ToString(), tenant.Status,
                tenant.Onboarding?.CurrentStep, tenant.Onboarding?.Status, OnboardingSteps.All,
                tenant.StatusChanges.OrderBy(c => c.ChangedAt)
                    .Select(c => new StatusChangeView(c.FromStatus, c.ToStatus, c.Reason, c.ChangedBy, c.ChangedAt)).ToList(),
                tenant.RowVersion, tenant.TimeZone);
    }
}

internal sealed class TenancyEventMapper : IDomainEventMapper
{
    public IIntegrationEvent? Map(IDomainEvent domainEvent) => domainEvent switch
    {
        Domain.Tenants.TenantSuspended suspended => new Contracts.TenantSuspended(suspended.TenantId),
        Domain.Tenants.TenantActivated activated => new Contracts.TenantActivated(activated.TenantId),
        _ => null,
    };
}

/// <summary>The tenant register for per-tenant background work such as the outbox relay. All tenants, including terminated ones, so pending events still drain.</summary>
internal sealed class TenantDirectory([FromKeyedServices(TenancyDbContext.SchemaName)] NpgsqlDataSource dataSource) : ITenantDirectory
{
    public async Task<IReadOnlyList<Guid>> GetActiveTenantIds(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT id FROM tenant.tenant ORDER BY id");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var ids = new List<Guid>();
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }
}

internal sealed class TenantStatusQuery([FromKeyedServices(TenancyDbContext.SchemaName)] NpgsqlDataSource dataSource) : ITenantStatusQuery
{
    public async Task<bool> IsActive(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT status FROM tenant.tenant WHERE id = @id");
        command.Parameters.AddWithValue("id", tenantId);
        return await command.ExecuteScalarAsync(cancellationToken) is string status && status == UpperCaseEnumConverter<TenantStatus>.ToCode(TenantStatus.Active);
    }

    public async Task<TenantUserAccess> UserAccess(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT status FROM tenant.tenant WHERE id = @id");
        command.Parameters.AddWithValue("id", tenantId);
        var status = await command.ExecuteScalarAsync(cancellationToken) as string;
        return status == UpperCaseEnumConverter<TenantStatus>.ToCode(TenantStatus.Active) ? TenantUserAccess.Full
            : status == UpperCaseEnumConverter<TenantStatus>.ToCode(TenantStatus.Onboarding) ? TenantUserAccess.ConfigurationOnly
            : TenantUserAccess.None;
    }
}

internal sealed class TenantLocaleDefaults([FromKeyedServices(TenancyDbContext.SchemaName)] NpgsqlDataSource dataSource)
    : ITenantBaseCurrency, ITenantTimeZone, ITenantFormatting, BuildingBlocks.Application.Context.ITenantTimeZoneSource
{
    public async Task<TenantFormatting?> Formatting(Guid tenantId, DateOnly day, CancellationToken cancellationToken) =>
        await Column("country", tenantId, day, cancellationToken) is { } country && await BaseCurrency(tenantId, day, cancellationToken) is { } currency
            ? new TenantFormatting(country, currency)
            : null;

    public Task<string?> BaseCurrency(Guid tenantId, DateOnly day, CancellationToken cancellationToken) => Column("base_currency", tenantId, day, cancellationToken);

    public Task<string?> TimeZone(Guid tenantId, DateOnly day, CancellationToken cancellationToken) => Column("time_zone", tenantId, day, cancellationToken);

    /// <summary>
    /// For the business date (GAP-044), read synchronously and cached by the building blocks for a minute. The locale
    /// defaults in force on the tenant's UTC date: the business date cannot be known before the zone is.
    /// </summary>
    public string? TimeZoneOf(Guid tenantId)
    {
        using var connection = dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var set = new NpgsqlCommand(TenantSession.SetTenantSql, connection, transaction))
        {
            set.Parameters.AddWithValue("tenantId", tenantId.ToString());
            set.ExecuteNonQuery();
        }

        // The locale defaults in force, else the zone given at registration (ADR-025); null for a tenant with neither.
        using var command = new NpgsqlCommand("""
            SELECT coalesce(
              (SELECT time_zone FROM tenant.cfg_locale_defaults
               WHERE tenant_id = @t AND record_status = ANY(@statuses) AND effective_from <= @day AND (effective_to IS NULL OR effective_to > @day)
               ORDER BY version DESC LIMIT 1),
              (SELECT time_zone FROM tenant.tenant WHERE id = @t))
            """, connection, transaction);
        command.Parameters.AddWithValue("t", tenantId);
        command.Parameters.AddWithValue("statuses", BuildingBlocks.Infrastructure.StaticData.StaticLifecycle.Configuration.InForce);
        command.Parameters.AddWithValue("day", DateOnly.FromDateTime(DateTime.UtcNow));
        var value = command.ExecuteScalar() as string;
        transaction.Commit();
        return value;
    }

    private async Task<string?> Column(string column, Guid tenantId, DateOnly day, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT {column} FROM tenant.cfg_locale_defaults
            WHERE tenant_id = @t AND record_status = ANY(@statuses) AND effective_from <= @day AND (effective_to IS NULL OR effective_to > @day)
            ORDER BY version DESC LIMIT 1
            """, connection, transaction);
        command.Parameters.AddWithValue("t", tenantId);
        command.Parameters.AddWithValue("statuses", BuildingBlocks.Infrastructure.StaticData.StaticLifecycle.Configuration.InForce);
        command.Parameters.AddWithValue("day", day);
        var value = await command.ExecuteScalarAsync(cancellationToken) as string;
        await transaction.CommitAsync(cancellationToken);
        return value;
    }
}

/// <summary>
/// A tenant's languages and default language from its locale defaults in force (docs/specs/addenda/localisation.md §1,
/// OQ-068). English only when none is in force. Cached for a minute, like the other per-request lookups.
/// </summary>
public sealed class TenantLanguageSettings([FromKeyedServices(TenancyDbContext.SchemaName)] NpgsqlDataSource dataSource, IMemoryCache cache, IClock clock)
    : ILanguageSettings
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(1);

    public async Task<TenantLanguages> For(Guid tenantId, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(("tenant-languages", tenantId), out TenantLanguages? cached) && cached is not null)
        {
            return cached;
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT languages, default_language FROM tenant.cfg_locale_defaults
            WHERE tenant_id = @t AND record_status = ANY(@statuses) AND effective_from <= @day AND (effective_to IS NULL OR effective_to > @day)
            ORDER BY version DESC LIMIT 1
            """, connection, transaction);
        command.Parameters.AddWithValue("t", tenantId);
        command.Parameters.AddWithValue("statuses", BuildingBlocks.Infrastructure.StaticData.StaticLifecycle.Configuration.InForce);
        command.Parameters.AddWithValue("day", clock.UtcToday);
        var result = TenantLanguages.EnglishOnly;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                var languages = reader.GetFieldValue<string[]>(0).Select(Normalise).Where(l => l.Length == 2).Distinct().ToList();
                var preferred = Normalise(reader.GetString(1));
                result = languages.Contains(preferred) ? new TenantLanguages(languages, preferred) : TenantLanguages.EnglishOnly;
            }
        }

        await transaction.CommitAsync(cancellationToken);
        cache.Set(("tenant-languages", tenantId), result, CacheFor);
        return result;
    }

    public void Invalidate(Guid tenantId) => cache.Remove(("tenant-languages", tenantId));

    private static string Normalise(string language) => language.Trim().ToLowerInvariant();
}

/// <summary>Drops a tenant's cached languages when a locale defaults version is activated, so the next request reads it.</summary>
public sealed class LocaleCachePolicy(TenantLanguageSettings languages, ITenantContext tenant) : IIntegrationEventHandler<StaticDataVersionActivated>
{
    public Task Handle(StaticDataVersionActivated activated, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (activated.Record is { Schema: TenancyDbContext.SchemaName, Table: "cfg_locale_defaults" })
        {
            languages.Invalidate(tenant.RequireTenantId());
        }

        return Task.CompletedTask;
    }
}

/// <summary>A locale defaults version's default language must be one of its languages (V0022; addendum §1).</summary>
internal sealed class LocaleDefaultsValidator : IStaticRecordValidator
{
    public string Schema => TenancyDbContext.SchemaName;

    public string Table => "cfg_locale_defaults";

    public IReadOnlyDictionary<string, string[]> Validate(StaticRecord record) =>
        record is LocaleDefaults locale && !locale.Languages.Contains(locale.DefaultLanguage, StringComparer.Ordinal)
            ? new Dictionary<string, string[]> { ["defaultLanguage"] = [$"Must be one of: {string.Join(", ", locale.Languages)}."] }
            : new Dictionary<string, string[]>();
}

internal sealed class TenantProfiles([FromKeyedServices(TenancyDbContext.SchemaName)] NpgsqlDataSource dataSource) : ITenantProfiles
{
    public async Task<TenantProfile?> Get(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT tenant_code, legal_name, status FROM tenant.tenant WHERE id = @id");
        command.Parameters.AddWithValue("id", tenantId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new TenantProfile(tenantId, reader.GetString(0), reader.GetString(1), UpperCaseEnumConverter<TenantStatus>.FromCode(reader.GetString(2)).ToString()) : null;
    }
}

/// <summary>
/// /health check tenant-time-zones (ADR-025): Degraded while any tenant has no time zone (neither locale defaults nor one
/// given at registration) and so runs on the default, Africa/Johannesburg; its data lists those tenants' codes to fix.
/// </summary>
internal sealed class TenantTimeZonesHealthCheck(TenantLocaleDefaults zones, [FromKeyedServices(TenancyDbContext.SchemaName)] NpgsqlDataSource dataSource)
    : Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck
{
    public async Task<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult> CheckHealthAsync(
        Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var missing = new List<string>();
        await using (var command = dataSource.CreateCommand("SELECT id, tenant_code FROM tenant.tenant ORDER BY tenant_code"))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            var tenants = new List<(Guid Id, string Code)>();
            while (await reader.ReadAsync(cancellationToken))
            {
                tenants.Add((reader.GetGuid(0), reader.GetString(1)));
            }

            await reader.CloseAsync();
            missing.AddRange(tenants.Where(t => string.IsNullOrWhiteSpace(zones.TimeZoneOf(t.Id))).Select(t => t.Code));
        }

        return missing.Count == 0
            ? Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy("Every tenant has a time zone.")
            : Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Degraded(
                $"{missing.Count} tenant(s) have no time zone and use the default, Africa/Johannesburg (ADR-025): {string.Join(", ", missing.Take(20))}",
                data: new Dictionary<string, object> { ["tenantsWithoutTimeZone"] = missing });
    }
}

/// <summary>A tenant's organisation structure in force (F103), read on the Tenancy module's own connection and cached for a minute.</summary>
internal sealed class OrganisationStructureQuery([FromKeyedServices(TenancyDbContext.SchemaName)] NpgsqlDataSource dataSource, IMemoryCache cache) : IOrganisationStructure
{
    public async Task<IReadOnlyList<OrganisationNode>> Nodes(Guid tenantId, DateOnly day, CancellationToken cancellationToken) =>
        await cache.GetOrCreateAsync($"org-structure:{tenantId:N}:{day:yyyyMMdd}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
            await using var command = new NpgsqlCommand("""
                SELECT DISTINCT ON (code) node_code, type, parent FROM tenant.mst_organisation_structure
                WHERE (tenant_id = @t OR tenant_id IS NULL) AND record_status = ANY(@statuses) AND effective_from <= @day AND (effective_to IS NULL OR effective_to > @day)
                ORDER BY code, (tenant_id IS NULL), version DESC
                """, connection, transaction);
            command.Parameters.AddWithValue("t", tenantId);
            command.Parameters.AddWithValue("statuses", BuildingBlocks.Infrastructure.StaticData.StaticLifecycle.Reference.InForce);
            command.Parameters.AddWithValue("day", day);
            var nodes = new List<OrganisationNode>();
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    nodes.Add(new OrganisationNode(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
                }
            }

            await transaction.CommitAsync(cancellationToken);
            return (IReadOnlyList<OrganisationNode>)nodes;
        }) ?? [];
}
