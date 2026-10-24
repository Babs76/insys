/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Infrastructure
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
using System.Data.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Insys.BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// The maximum pool size of each module role's connections (GAP-039, OQ-069 option A). Each instance opens one pool per
/// role, so their maxima must fit the database's max_connections, less a reserve, divided by the instances.
/// Precedence: Database:Pools:&lt;role&gt;, then Maximum Pool Size in the role's connection string, then the table below.
/// </summary>
public static class DatabasePools
{
    /// <summary>OQ-069 option A: hot paths 8, medium 4, the rest 3; 119 per instance for the 24 roles.</summary>
    public static readonly IReadOnlyDictionary<string, int> Defaults = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["insys_api"] = 8, ["insys_iam"] = 8, ["insys_payment"] = 8, ["insys_billing"] = 8, ["insys_rating"] = 8, ["insys_health"] = 8, ["insys_general"] = 8,
        ["insys_integration"] = 8,
        ["insys_party"] = 4, ["insys_product"] = 4, ["insys_claims"] = 4, ["insys_life"] = 4, ["insys_finance"] = 4, ["insys_workflow"] = 4, ["insys_document"] = 4,
        ["insys_sdm"] = 3, ["insys_rules"] = 3, ["insys_commission"] = 3, ["insys_reinsurance"] = 3, ["insys_reporting"] = 3, ["insys_analytics"] = 3,
        ["insys_tenant"] = 3, ["insys_audit"] = 3, ["insys_communication"] = 3,
    };

    /// <summary>A role with no default (a module added later) gets this until it is given one.</summary>
    public const int Fallback = 3;

    /// <summary>The roles this process opens pools for, as their data sources are registered.</summary>
    public static ConcurrentBag<string> Roles { get; } = [];

    private static readonly string[] MaxPoolKeys = ["Maximum Pool Size", "Max Pool Size", "MaxPoolSize"];

    public static int MaxPoolSize(IConfiguration configuration, string role, string connectionString)
    {
        if (configuration.GetValue<int?>($"Database:Pools:{role}") is { } configured)
        {
            return configured;
        }

        var parsed = new DbConnectionStringBuilder { ConnectionString = connectionString };
        foreach (var key in MaxPoolKeys)
        {
            if (parsed.TryGetValue(key, out var value) && int.TryParse(value?.ToString(), out var explicitSize))
            {
                return explicitSize;
            }
        }

        return Defaults.TryGetValue(role, out var size) ? size : Fallback;
    }

    /// <summary>The per-instance budget: (max_connections − reserve) ÷ instances, rounded down.</summary>
    public static int Budget(int maxConnections, int reserve, int instances) => (maxConnections - reserve) / Math.Max(1, instances);
}

/// <summary>
/// Refuses to start when this instance's pool maxima, added up, exceed its share of the database's connections
/// (GAP-039), given Database:MaxConnections, Database:Reserve (default 20: superuser, RDS agents, the migration runner,
/// psql) and Database:Instances (default 1, plus one during a rolling deployment). Without Database:MaxConnections it only logs the total.
/// </summary>
public sealed class DatabasePoolGuard(IConfiguration configuration, ILogger<DatabasePoolGuard> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var template = configuration.GetConnectionString("Template") ?? "";
        var pools = DatabasePools.Roles.Distinct(StringComparer.Ordinal)
            .ToDictionary(r => r, r => DatabasePools.MaxPoolSize(configuration, r, configuration.GetConnectionString(r) is { Length: > 0 } own ? own : template),
                StringComparer.Ordinal);
        var total = pools.Values.Sum();
        if (configuration.GetValue<int?>("Database:MaxConnections") is not { } max)
        {
            logger.LogInformation("Database pools: {Pools} roles, at most {Total} connections per instance", pools.Count, total);
            return Task.CompletedTask;
        }

        var reserve = configuration.GetValue("Database:Reserve", 20);
        var instances = configuration.GetValue("Database:Instances", 1);
        var budget = DatabasePools.Budget(max, reserve, instances);
        if (total > budget)
        {
            throw new InvalidOperationException(
                $"The database pools add up to {total} connections per instance, more than this instance's share: ({max} max_connections − {reserve} reserve) ÷ "
                + $"{instances} instances = {budget}. Lower Database:Pools:<role>, raise max_connections, or plan fewer instances (GAP-039, OQ-069).");
        }

        logger.LogInformation("Database pools: {Total} connections per instance, within {Budget} of {Max}", total, budget, max);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
