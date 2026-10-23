/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Party
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
using Insys.BuildingBlocks.Application.Messaging;
using Insys.Party.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Insys.Party.Infrastructure;

/// <summary>Applies the party search log's retention (OQ-073) for every active tenant, each in its own scope and transaction.</summary>
public sealed class SearchLogRetentionJob(IServiceScopeFactory scopes, ITenantDirectory tenants, ILogger<SearchLogRetentionJob> logger)
{
    public const string ServiceName = "party-search-log-retention";

    public async Task<int> Run(CancellationToken cancellationToken)
    {
        var runs = 0;
        foreach (var tenantId in await tenants.GetActiveTenantIds(cancellationToken))
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ExecutionScope>().InitialiseForService(tenantId, ServiceName);
            var result = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new ApplySearchLogRetention(), cancellationToken);
            if (result.IsSuccess)
            {
                runs++;
            }
            else
            {
                logger.LogError("Party search log retention failed for tenant {TenantId}: {Code}", tenantId, result.Error.Code);
            }
        }

        return runs;
    }
}

public sealed class SearchLogRetentionService(SearchLogRetentionJob job, IOptions<SearchLogRetentionOptions> options, ILogger<SearchLogRetentionService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.JobEnabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(options.Value.JobInterval);
        do
        {
            try
            {
                await job.Run(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("Party search log retention failed: {ExceptionType}", ex.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
