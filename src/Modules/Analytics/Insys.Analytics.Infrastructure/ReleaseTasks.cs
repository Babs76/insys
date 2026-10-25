/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Analytics
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
using Insys.Analytics.Application;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Insys.Analytics.Infrastructure;

/// <summary>
/// Runs Analytics' release tasks for each active tenant once the host has started (Wave 2 batch 1): today, the rebuild by
/// business date (ADR-025). Each runs in its own transaction and is recorded in analytics.release_task with its work, so it
/// never runs twice; a tenant that fails is retried at the next start. Analytics:ReleaseTasksEnabled turns it off (tests).
/// </summary>
public sealed class AnalyticsReleaseTasks(IServiceScopeFactory scopes, ITenantDirectory tenants, ILogger<AnalyticsReleaseTasks> logger)
{
    /// <summary>Runs the pending release tasks for one tenant; returns the entries replayed, or null when already done.</summary>
    public async Task<int?> RunFor(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ExecutionScope>().InitialiseForService(tenantId, "analytics-release-tasks");
        var result = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new RunReleaseRebuild(), cancellationToken);
        return result.IsSuccess ? result.Value : throw new InvalidOperationException($"{RunReleaseRebuild.TaskCode} failed: {result.Error!.Code}");
    }

    public async Task RunAll(CancellationToken cancellationToken)
    {
        foreach (var tenantId in await tenants.GetActiveTenantIds(cancellationToken))
        {
            try
            {
                if (await RunFor(tenantId, cancellationToken) is { } replayed)
                {
                    logger.LogInformation("Release task {Task} ran for tenant {TenantId}: {Replayed} entries replayed", RunReleaseRebuild.TaskCode, tenantId, replayed);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("Release task {Task} failed for tenant {TenantId}: {ExceptionType}; it runs again at the next start",
                    RunReleaseRebuild.TaskCode, tenantId, ex.GetType().Name);
            }
        }
    }
}

public sealed class AnalyticsReleaseTasksService(AnalyticsReleaseTasks tasks, IConfiguration configuration, IHostApplicationLifetime lifetime) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (configuration.GetValue("Analytics:ReleaseTasksEnabled", true))
        {
            lifetime.ApplicationStarted.Register(() => _ = Task.Run(() => tasks.RunAll(lifetime.ApplicationStopping)));
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
