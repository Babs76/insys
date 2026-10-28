/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Rating
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
using System.Text.Json;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Contracts.StaticData;
using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.ProductFactory.Contracts;
using Insys.Rating.Application;
using Insys.Rating.Contracts;
using Insys.Rating.Domain.Calculations;
using Insys.Rating.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;

namespace Insys.Rating.Infrastructure.Persistence;

internal sealed class PremiumCalculationRepository(RatingDbContext db) : IPremiumCalculationRepository
{
    public void Add(PremiumCalculation calculation) => db.Calculations.Add(calculation);

    public void NotCalculated(Contracts.PremiumNotCalculated failure, Guid subjectId, DateTimeOffset now) => db.Publish(failure, "PremiumCalculation", subjectId, now);
}

internal sealed class PremiumCalculationQueries(RatingDbContext db) : IPremiumCalculationQueries
{
    public async Task<CalculationView?> Get(Guid id, CancellationToken cancellationToken) =>
        await db.Calculations.AsNoTracking().Include(c => c.Lines).SingleOrDefaultAsync(c => c.Id == id, cancellationToken) is { } calculation
            ? CalculationViews.From(calculation)
            : null;

    // Newest first by ID (UUIDv7, which sorts by creation time), so the cursor and the order agree (BR-025).
    public async Task<IReadOnlyList<CalculationView>> ForSubject(string subjectType, Guid subjectId, Guid? beforeId, int limit, CancellationToken cancellationToken) =>
        (await db.Calculations.AsNoTracking().Include(c => c.Lines)
            .Where(c => c.SubjectType == subjectType && c.SubjectId == subjectId && (beforeId == null || c.Id.CompareTo(beforeId.Value) < 0))
            .OrderByDescending(c => c.Id).Take(limit).ToListAsync(cancellationToken)).Select(CalculationViews.From).ToList();
}

internal sealed class RatingTestCases(RatingDbContext db) : IRatingTestCases
{
    public async Task<IReadOnlyList<RatingTestCase>> For(string productCode, int productVersion, DateOnly day, CancellationToken cancellationToken)
    {
        var reference = RatingConfiguration.ProductVersionRef(productCode, productVersion);
        return (await db.Set<RatingTestCaseLibrary>().AsNoTracking().InForce(RatingConfigurationReader.Lifecycle<RatingTestCaseLibrary>(), day)
                .Where(c => c.ProductVersion == reference).OrderBy(c => c.CaseId).ToListAsync(cancellationToken))
            .Select(c => new RatingTestCase(c.CaseId, c.Inputs, c.ExpectedPremium, c.Tolerance)).ToList();
    }
}

/// <summary>Each instance keeps rating configuration for 10 minutes, evicted per tenant on change (ADR-006: one instance).</summary>
public sealed class RatingConfigurationCache(IMemoryCache cache)
{
    private static readonly TimeSpan Duration = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _tenantTokens = new();

    public bool TryGet(string key, out RatingConfiguration? configuration) => cache.TryGetValue(key, out configuration);

    public void Set(Guid tenantId, string key, RatingConfiguration configuration)
    {
        var token = _tenantTokens.GetOrAdd(tenantId, _ => new CancellationTokenSource());
        cache.Set(key, configuration, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = Duration,
            ExpirationTokens = { new CancellationChangeToken(token.Token) },
        });
    }

    public void EvictTenant(Guid tenantId)
    {
        if (_tenantTokens.TryRemove(tenantId, out var token))
        {
            token.Cancel();
            token.Dispose();
        }
    }
}

/// <summary>Builds the rating configuration in force for a product version from the Rating module's static data (ADR-007).</summary>
internal sealed class RatingConfigurationReader(RatingDbContext db, RatingConfigurationCache cache, ITenantContext tenant) : IRatingConfigurationReader
{
    public static StaticLifecycle Lifecycle<T>() => RatingStaticData.Descriptors.Single(d => d.EntityType == typeof(T)).Lifecycle;

    public async Task<Result<RatingConfiguration>> Read(string productCode, int productVersion, string currency, DateOnly day, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var key = $"rating:{tenantId:N}:{productCode}:{productVersion}:{currency}:{day:yyyy-MM-dd}";
        if (cache.TryGet(key, out var cached) && cached is not null)
        {
            return cached;
        }

        var built = await Build(productCode, productVersion, currency, day, cancellationToken);
        if (built.IsSuccess)
        {
            cache.Set(tenantId, key, built.Value);
        }

        return built;
    }

    public void Invalidate(Guid tenantId) => cache.EvictTenant(tenantId);

    private async Task<Result<RatingConfiguration>> Build(string productCode, int productVersion, string currency, DateOnly day, CancellationToken cancellationToken)
    {
        var reference = RatingConfiguration.ProductVersionRef(productCode, productVersion);
        var parameters = await InForce<PremiumParameters>(day).Where(p => p.ProductVersion == reference).ToListAsync(cancellationToken);
        var factors = new List<FactorDefinition>();
        foreach (var factor in await InForce<RatingFactorDefinitions>(day).ToListAsync(cancellationToken))
        {
            factors.Add(new FactorDefinition(factor.FactorCode, factor.DataType, factor.SourceAttribute, Bands(factor.Banding)));
        }

        var codes = parameters.Select(p => p.ParameterCode).ToArray();
        var tables = new List<RatingTable>();
        foreach (var table in await InForce<RatingTables>(day).Where(t => codes.Contains(t.TableCode)).ToListAsync(cancellationToken))
        {
            var entries = Entries(table.Values, table.Dimensions.Length);
            if (entries is null)
            {
                return Error.Validation(RatingErrorCodes.ConfigurationIncomplete,
                    $"Rating table {table.TableCode} v{table.Version} values must be a list of {{\"key\": [...], \"value\": n}} with one key part per dimension.");
            }

            tables.Add(new RatingTable(table.TableCode, table.Version, table.Dimensions, entries, table.Interpolation));
        }

        var adjustments = (await InForce<DiscountAndLoadingSchedules>(day).ToListAsync(cancellationToken))
            .Select(a => new Adjustment(a.Code, a.Type, a.Basis, a.Value, a.Cap, a.StackingOrder)).ToList();
        var bounds = (await InForce<StaticData.PremiumBounds>(day).Where(b => b.ProductCode == productCode && b.Currency == currency).ToListAsync(cancellationToken))
            .GroupBy(b => b.Frequency).ToDictionary(g => g.Key, g => new Domain.Calculations.PremiumBounds(g.First().Minimum, g.First().Maximum), StringComparer.Ordinal);
        var taxes = (await InForce<PremiumTaxAndLevyRates>(day).ToListAsync(cancellationToken))
            .Select(t => (t.Jurisdiction, new TaxRate(t.TaxCode, t.Rate, t.Base, t.Rounding))).ToList();
        var roundings = (await InForce<RoundingAndCurrencyPrecisionRules>(day).Where(r => r.Currency == currency).ToListAsync(cancellationToken))
            .Select(r => new Rounding(r.RuleCode, r.Step, r.Method, r.Stage)).ToList();

        return new RatingConfiguration(productCode, productVersion, factors, tables,
            parameters.Select(p => new PremiumParameter(p.ParameterCode, p.Value, p.Unit)).ToList(), adjustments, bounds, taxes, roundings);
    }

    private IQueryable<T> InForce<T>(DateOnly day) where T : StaticRecord => db.Set<T>().AsNoTracking().InForce(Lifecycle<T>(), day);

    /// <summary>Bands as a list of labels ("18-24", "60+"), a comma-separated string, or objects with a label.</summary>
    private static List<Band> Bands(JsonElement? banding) => banding switch
    {
        { ValueKind: JsonValueKind.Array } array => array.EnumerateArray()
            .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.TryGetProperty("label", out var l) ? l.GetString() : null)
            .Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => Band.Parse(l!)).ToList(),
        { ValueKind: JsonValueKind.String } text => text.GetString()!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Band.Parse).ToList(),
        _ => [],
    };

    private static List<TableEntry>? Entries(JsonElement values, int dimensions)
    {
        if (values.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var entries = new List<TableEntry>();
        foreach (var item in values.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("key", out var key) || key.ValueKind != JsonValueKind.Array
                || key.GetArrayLength() != dimensions || !item.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Number)
            {
                return null;
            }

            entries.Add(new TableEntry(key.EnumerateArray().Select(k => k.ValueKind == JsonValueKind.String ? k.GetString()! : k.GetRawText()).ToList(), value.GetDecimal()));
        }

        return entries;
    }
}

/// <summary>Dry-run rating for other modules, through Rating's own read-only transaction (IPremiumSimulator).</summary>
internal sealed class PremiumSimulator(IDispatcher dispatcher) : IPremiumSimulator
{
    public async Task<SimulatedPremium> Simulate(RatingRequest request, CancellationToken cancellationToken)
    {
        var result = await dispatcher.Query(new SimulatePremium(request), cancellationToken);
        return result.IsSuccess
            ? new SimulatedPremium(true, result.Value.Premium, result.Value.Currency, result.Value.RatingVersion, null, null)
            : new SimulatedPremium(false, null, request.Currency, 0, result.Error.Code, result.Error.Message);
    }

    public async Task<IReadOnlyList<RatingTestCaseResult>> RunTestCases(string productCode, int productVersion, DateOnly day, CancellationToken cancellationToken)
    {
        var result = await dispatcher.Query(new RunRatingTestCases(productCode, productVersion, day), cancellationToken);
        return result.IsSuccess ? result.Value : [];
    }
}

/// <summary>
/// Rating's policies. On ProductVersionPublished ("Load new rating version into cache", domain-model.json) the
/// tenant's cached configuration is dropped, so the next calculation loads the configuration in force, including the
/// new version; a change to rating static data does the same.
/// </summary>
public sealed class RatingCachePolicies(IRatingConfigurationReader reader, ITenantContext tenant) :
    IIntegrationEventHandler<ProductVersionPublished>,
    IIntegrationEventHandler<StaticDataVersionActivated>
{
    public Task Handle(ProductVersionPublished published, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        reader.Invalidate(tenant.RequireTenantId());
        return Task.CompletedTask;
    }

    public Task Handle(StaticDataVersionActivated activated, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (activated.Record.Schema == RatingDbContext.SchemaName)
        {
            reader.Invalidate(tenant.RequireTenantId());
        }

        return Task.CompletedTask;
    }
}

internal sealed class RatingEventMapper : IDomainEventMapper
{
    public IIntegrationEvent? Map(IDomainEvent domainEvent) => domainEvent switch
    {
        Domain.Calculations.PremiumCalculated e => new Contracts.PremiumCalculated(e.CalculationId, e.RequestRef, e.SourceDomain, e.SubjectType, e.SubjectId,
            e.ProductCode, e.ProductVersion, e.RatingVersion, e.Premium, e.Currency),
        _ => null,
    };
}
