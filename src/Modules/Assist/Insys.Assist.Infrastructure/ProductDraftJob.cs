/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Assist
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
using Insys.Assist.Application;
using Insys.BuildingBlocks.Application.Context;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Insys.Assist.Infrastructure;

/// <summary>AI-1's job (AIA-017) over every tenant: each tenant's queued runs, acting for the user who asked.</summary>
public sealed class ProductDraftJob(ProductDraftWorker worker, ITenantDirectory tenants, ILogger<ProductDraftJob> logger)
{
    public async Task<int> Run(CancellationToken cancellationToken)
    {
        var finished = 0;
        foreach (var tenantId in await tenants.GetActiveTenantIds(cancellationToken))
        {
            try
            {
                finished += await worker.RunTenant(tenantId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A run that threw stays RUNNING and ends as stalled; nothing of its documents is logged.
                logger.LogError("AI-1's job failed for tenant {TenantId}: {ExceptionType}", tenantId, ex.GetType().Name);
            }
        }

        return finished;
    }
}

public sealed class ProductDraftService(ProductDraftJob job, IOptions<ProductDraftOptions> options, ILogger<ProductDraftService> logger) : BackgroundService
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
                logger.LogError("AI-1's job failed: {ExceptionType}", ex.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

/// <summary>AI-3's job (7.8) over every tenant: each tenant's queued passes, acting for the user who asked (AIA-017, as AI-1's).</summary>
public sealed class SetupAiJob(SetupAiWorker worker, ITenantDirectory tenants, ILogger<SetupAiJob> logger)
{
    public async Task<int> Run(CancellationToken cancellationToken)
    {
        var finished = 0;
        foreach (var tenantId in await tenants.GetActiveTenantIds(cancellationToken))
        {
            try
            {
                finished += await worker.RunTenant(tenantId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A pass that threw stays RUNNING and ends as stalled; nothing of its documents is logged.
                logger.LogError("AI-3's job failed for tenant {TenantId}: {ExceptionType}", tenantId, ex.GetType().Name);
            }
        }

        return finished;
    }
}

public sealed class SetupAiService(SetupAiJob job, IOptions<SetupAiOptions> options, ILogger<SetupAiService> logger) : BackgroundService
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
                logger.LogError("AI-3's job failed: {ExceptionType}", ex.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
