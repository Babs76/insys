/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Identity
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
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Identity.Infrastructure.Persistence;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.Identity.Infrastructure;

/// <summary>A function in force in the catalogue (iam.ref_function_catalogue, ADR-026).</summary>
public sealed record CatalogueEntry(string Code, string Kind, string SubDomain, string ProcessArea, string? Parent, IReadOnlyList<string> Audiences, string? RouteKey,
    IReadOnlyList<string> Permissions, string? Feature, bool Privileged, bool AllowedWhileOnboarding, string? Labels, int SortOrder);

/// <summary>A domain or sub-domain of the menu (iam.ref_business_domains, iam.ref_business_sub_domains).</summary>
public sealed record TaxonomyEntry(string Code, string? DomainCode, string? Icon, string? Labels, int SortOrder);

/// <summary>
/// Reads the global function catalogue and taxonomy in force on a day, cached for the permission cache's time: they
/// change only with a release or a platform change, and an activation evicts them (ADR-026 §3).
/// </summary>
public sealed class FunctionCatalogueReader([FromKeyedServices(IdentityDbContext.SchemaName)] NpgsqlDataSource dataSource, IMemoryCache cache)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);
    private static readonly string[] Statuses = StaticLifecycle.Reference.InForce;
    private CancellationTokenSource _token = new();

    public Task<IReadOnlyDictionary<string, CatalogueEntry>> Functions(DateOnly day, CancellationToken cancellationToken) =>
        Cached($"function-catalogue:{day:yyyyMMdd}", () => LoadFunctions(day, cancellationToken));

    public Task<IReadOnlyList<TaxonomyEntry>> Domains(DateOnly day, CancellationToken cancellationToken) =>
        Cached($"business-domains:{day:yyyyMMdd}", () => LoadTaxonomy("iam.ref_business_domains", "NULL", day, cancellationToken));

    public Task<IReadOnlyList<TaxonomyEntry>> SubDomains(DateOnly day, CancellationToken cancellationToken) =>
        Cached($"business-sub-domains:{day:yyyyMMdd}", () => LoadTaxonomy("iam.ref_business_sub_domains", "domain_code", day, cancellationToken));

    public void Evict()
    {
        var old = Interlocked.Exchange(ref _token, new CancellationTokenSource());
        old.Cancel();
        old.Dispose();
    }

    private async Task<T> Cached<T>(string key, Func<Task<T>> load)
    {
        if (cache.TryGetValue(key, out T? value) && value is not null)
        {
            return value;
        }

        value = await load();
        cache.Set(key, value, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = CacheFor,
            ExpirationTokens = { new Microsoft.Extensions.Primitives.CancellationChangeToken(_token.Token) },
        });
        return value;
    }

    private async Task<IReadOnlyDictionary<string, CatalogueEntry>> LoadFunctions(DateOnly day, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT DISTINCT ON (function_code) function_code, function_kind, sub_domain_code, process_area, parent_function_code, audiences, route_key,
                   required_permissions, required_feature, privileged, allowed_while_onboarding, labels::text, coalesce(sort_order, 0)
            FROM iam.ref_function_catalogue
            WHERE tenant_id IS NULL AND record_status = ANY(@statuses) AND effective_from <= @day AND (effective_to IS NULL OR effective_to > @day)
            ORDER BY function_code, version DESC
            """, connection);
        command.Parameters.AddWithValue("statuses", Statuses);
        command.Parameters.AddWithValue("day", day);
        var functions = new Dictionary<string, CatalogueEntry>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var code = reader.GetString(0);
            functions[code] = new CatalogueEntry(code, reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetFieldValue<string[]>(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetFieldValue<string[]>(7),
                reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetBoolean(9), reader.GetBoolean(10), reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.GetInt32(12));
        }

        return functions;
    }

    private async Task<IReadOnlyList<TaxonomyEntry>> LoadTaxonomy(string table, string domainColumn, DateOnly day, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT DISTINCT ON (code) code, {domainColumn}, icon, labels::text, coalesce(sort_order, 0) FROM {table}
            WHERE tenant_id IS NULL AND record_status = ANY(@statuses) AND effective_from <= @day AND (effective_to IS NULL OR effective_to > @day)
            ORDER BY code, version DESC
            """, connection);
        command.Parameters.AddWithValue("statuses", Statuses);
        command.Parameters.AddWithValue("day", day);
        var entries = new List<TaxonomyEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(new TaxonomyEntry(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt32(4)));
        }

        return entries.OrderBy(e => e.SortOrder).ThenBy(e => e.Code, StringComparer.Ordinal).ToList();
    }
}

/// <summary>A permission is covered when a function needing only permissions of the set needs it.</summary>
internal sealed class FunctionCoverage(FunctionCatalogueReader catalogue) : Application.Users.IFunctionCoverage
{
    public async Task<IReadOnlyList<string>> Uncovered(IReadOnlyCollection<string> permissions, DateOnly day, CancellationToken cancellationToken)
    {
        var functions = await catalogue.Functions(day, cancellationToken);
        var granted = Persistence.TenantAdministratorStore.FunctionsWithin(functions, permissions).SelectMany(f => functions[f].Permissions)
            .ToHashSet(StringComparer.Ordinal);
        return permissions.Where(p => !granted.Contains(p)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    }
}

/// <summary>The menu from the catalogue and taxonomy in force (ADR-026 §3).</summary>
internal sealed class MenuBuilder(FunctionCatalogueReader catalogue) : Application.Users.IMenuBuilder
{
    public async Task<Application.Users.MenuView> Build(IReadOnlySet<string> usable, string audience, IReadOnlyList<string> languages, DateOnly day,
        CancellationToken cancellationToken)
    {
        var functions = await catalogue.Functions(day, cancellationToken);
        var held = functions.Values.Where(f => usable.Contains(f.Code) && f.Audiences.Contains(audience)).ToList();
        var screens = held.Where(f => f.Kind == "SCREEN").OrderBy(f => f.SortOrder).ThenBy(f => f.Code, StringComparer.Ordinal)
            .Select(f => (f.SubDomain, Screen: new Application.Users.MenuScreen(f.Code, $"function.{f.Code}", Label(f.Labels, languages, f.Code), f.RouteKey,
                held.Where(a => a.Kind == "ACTION" && a.Parent == f.Code).OrderBy(a => a.SortOrder).ThenBy(a => a.Code, StringComparer.Ordinal)
                    .Select(a => new Application.Users.MenuAction(a.Code, $"function.{a.Code}", Label(a.Labels, languages, a.Code))).ToList())))
            .ToList();
        var subDomains = (await catalogue.SubDomains(day, cancellationToken))
            .Select(d => (d.DomainCode, SubDomain: new Application.Users.MenuSubDomain(d.Code, $"menu.subDomain.{d.Code}", Label(d.Labels, languages, d.Code), d.Icon,
                screens.Where(s => s.SubDomain == d.Code).Select(s => s.Screen).ToList())))
            .Where(d => d.SubDomain.Screens.Count > 0).ToList();
        var domains = (await catalogue.Domains(day, cancellationToken))
            .Select(d => new Application.Users.MenuDomain(d.Code, $"menu.domain.{d.Code}", Label(d.Labels, languages, d.Code), d.Icon,
                subDomains.Where(s => s.DomainCode == d.Code).Select(s => s.SubDomain).ToList()))
            .Where(d => d.SubDomains.Count > 0).ToList();
        return new Application.Users.MenuView(audience, domains);
    }

    /// <summary>The label in the first language of the chain that has one, else the code.</summary>
    private static string Label(string? labels, IReadOnlyList<string> languages, string code)
    {
        if (labels is null)
        {
            return code;
        }

        var texts = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(labels) ?? [];
        return languages.Append("en").Select(l => texts.GetValueOrDefault(l)).FirstOrDefault(t => !string.IsNullOrEmpty(t)) ?? code;
    }
}

/// <summary>
/// Whether a tenant may run a JOB function (ADR-026 §2), from the catalogue and the tenant's settings, cached per tenant
/// for the permission cache's time; an activated availability or entitlement evicts it.
/// </summary>
public sealed class FunctionAvailability(FunctionCatalogueReader catalogue, Tenancy.Contracts.ITenantFunctionSettings settings,
    Microsoft.Extensions.Options.IOptions<BuildingBlocks.Application.Context.PlatformOptions> platform, BuildingBlocks.Application.Context.IClock clock)
    : BuildingBlocks.Application.Context.IFunctionAvailability
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (DateTimeOffset At, Tenancy.Contracts.TenantFunctionSettings Settings)> _tenants = new();

    public async Task<bool> IsEnabled(Guid tenantId, string functionCode, CancellationToken cancellationToken)
    {
        var day = clock.UtcToday;
        if (!(await catalogue.Functions(day, cancellationToken)).TryGetValue(functionCode, out var function))
        {
            return false;
        }

        var now = clock.UtcNow;
        if (!_tenants.TryGetValue(tenantId, out var cached) || now - cached.At > TimeSpan.FromSeconds(30))
        {
            cached = (now, await settings.For(tenantId, day, cancellationToken));
            _tenants[tenantId] = cached;
        }

        return !cached.Settings.DisabledFunctions.Contains(functionCode)
            && (function.Feature is null || tenantId == platform.Value.OperatorTenantId || cached.Settings.EntitledFeatures.Contains(function.Feature));
    }

    public void Evict(Guid? tenantId)
    {
        if (tenantId is { } id)
        {
            _tenants.TryRemove(id, out _);
        }
        else
        {
            _tenants.Clear();
        }
    }
}
