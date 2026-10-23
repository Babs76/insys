/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Audit
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
using Insys.Audit.Infrastructure.Persistence;
using Insys.BuildingBlocks.Application.Context;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Insys.Audit.Infrastructure.Partitions;

public sealed class PartitionOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Create partitions at least this many months ahead (OQ-005: 3).</summary>
    public int MonthsAhead { get; set; } = 3;

    /// <summary>Warn when fewer than this many months remain (OQ-005: 2).</summary>
    public int WarnBelowMonths { get; set; } = 2;

    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(12);
}

/// <summary>
/// Creates monthly partitions ahead of need for the eight partitioned tables (DBA-018, DBA-023), through the
/// SECURITY DEFINER function audit.ensure_monthly_partitions, which gives each partition the parent's tenant policy.
/// The Audit role has no DDL rights of its own.
/// </summary>
public sealed class PartitionMaintainer([FromKeyedServices(AuditDbContext.SchemaName)] NpgsqlDataSource dataSource, IOptions<PartitionOptions> options)
{
    public async Task<IReadOnlyList<string>> EnsurePartitions(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT * FROM audit.ensure_monthly_partitions(@months)");
        command.Parameters.AddWithValue("months", options.Value.MonthsAhead);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var created = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            created.Add(reader.GetString(0));
        }

        return created;
    }

    /// <summary>The end of the furthest partition of each partitioned table.</summary>
    public async Task<IReadOnlyDictionary<string, DateOnly?>> Horizon(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT parent, covered_until FROM audit.partition_horizon()");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var horizon = new Dictionary<string, DateOnly?>();
        while (await reader.ReadAsync(cancellationToken))
        {
            horizon[reader.GetString(0)] = reader.IsDBNull(1) ? null : DateOnly.FromDateTime(reader.GetDateTime(1));
        }

        return horizon;
    }
}

/// <summary>Runs the partition job at start-up and then on a timer.</summary>
public sealed class PartitionMaintenanceService(PartitionMaintainer maintainer, IOptions<PartitionOptions> options, ILogger<PartitionMaintenanceService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(options.Value.Interval);
        do
        {
            try
            {
                var created = await maintainer.EnsurePartitions(stoppingToken);
                if (created.Count > 0)
                {
                    logger.LogInformation("Created {Count} partitions: {Partitions}", created.Count, string.Join(", ", created));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("Partition maintenance failed: {ExceptionType}", ex.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

/// <summary>Degraded (a warning, not a failure) when any partitioned table is covered for fewer than two months ahead (DBA-023).</summary>
public sealed class PartitionHorizonHealthCheck(PartitionMaintainer maintainer, IClock clock, IOptions<PartitionOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var horizon = await maintainer.Horizon(cancellationToken);
        var limit = clock.UtcToday.AddMonths(options.Value.WarnBelowMonths);
        var short_ = horizon.Where(h => h.Value is null || h.Value < limit).Select(h => $"{h.Key} until {h.Value?.ToString("yyyy-MM-dd") ?? "none"}").ToList();
        return short_.Count == 0
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Degraded($"Partitions cover less than {options.Value.WarnBelowMonths} months ahead: {string.Join("; ", short_)}");
    }
}
