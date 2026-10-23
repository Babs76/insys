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
        return rows.Select(t => new TenantSummary(t.Id, t.TenantCode, t.LegalName, t.IsolationProfile.ToString(), t.Status.ToString(), t.RowVersion)).ToList();
    }

    public async Task<TenantDetail?> Get(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants.AsNoTracking().Include("_onboardingCases").Include(t => t.StatusChanges)
            .SingleOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        return tenant is null
            ? null
            : new TenantDetail(tenant.Id, tenant.TenantCode, tenant.LegalName, tenant.IsolationProfile.ToString(), tenant.Status.ToString(),
                tenant.Onboarding?.CurrentStep, tenant.Onboarding?.Status, OnboardingSteps.All,
                tenant.StatusChanges.OrderBy(c => c.ChangedAt)
                    .Select(c => new StatusChangeView(c.FromStatus.ToString(), c.ToStatus.ToString(), c.Reason, c.ChangedBy, c.ChangedAt)).ToList(),
                tenant.RowVersion);
    }
}

internal sealed class TenancyEventMapper : IDomainEventMapper
{
    public IIntegrationEvent? Map(IDomainEvent domainEvent) => domainEvent switch
    {
        Domain.Tenants.TenantSuspended suspended => new Contracts.TenantSuspended(suspended.TenantId),
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
}

internal sealed class TenantBaseCurrency([FromKeyedServices(TenancyDbContext.SchemaName)] NpgsqlDataSource dataSource) : ITenantBaseCurrency
{
    public async Task<string?> BaseCurrency(Guid tenantId, DateOnly day, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT base_currency FROM tenant.cfg_locale_defaults
            WHERE tenant_id = @t AND record_status = ANY(@statuses) AND effective_from <= @day AND (effective_to IS NULL OR effective_to > @day)
            ORDER BY version DESC LIMIT 1
            """, connection, transaction);
        command.Parameters.AddWithValue("t", tenantId);
        command.Parameters.AddWithValue("statuses", BuildingBlocks.Infrastructure.StaticData.StaticLifecycle.Configuration.InForce);
        command.Parameters.AddWithValue("day", day);
        var currency = await command.ExecuteScalarAsync(cancellationToken) as string;
        await transaction.CommitAsync(cancellationToken);
        return currency;
    }
}

/// <summary>
/// A tenant's languages from its locale defaults in force; the first is the default (docs/specs/addenda/localisation.md
/// §1, OQ-068). English only when none is in force. Cached for a minute, like the other per-request lookups.
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
            SELECT languages FROM tenant.cfg_locale_defaults
            WHERE tenant_id = @t AND record_status = ANY(@statuses) AND effective_from <= @day AND (effective_to IS NULL OR effective_to > @day)
            ORDER BY version DESC LIMIT 1
            """, connection, transaction);
        command.Parameters.AddWithValue("t", tenantId);
        command.Parameters.AddWithValue("statuses", BuildingBlocks.Infrastructure.StaticData.StaticLifecycle.Configuration.InForce);
        command.Parameters.AddWithValue("day", clock.UtcToday);
        var languages = await command.ExecuteScalarAsync(cancellationToken) is string[] list
            ? list.Select(l => l.Trim().ToLowerInvariant()).Where(l => l.Length == 2).Distinct().ToList()
            : [];
        await transaction.CommitAsync(cancellationToken);
        var result = languages.Count > 0 ? new TenantLanguages(languages) : TenantLanguages.EnglishOnly;
        cache.Set(("tenant-languages", tenantId), result, CacheFor);
        return result;
    }

    public void Invalidate(Guid tenantId) => cache.Remove(("tenant-languages", tenantId));
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
