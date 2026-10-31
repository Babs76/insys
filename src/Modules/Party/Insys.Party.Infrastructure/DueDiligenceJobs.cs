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
using Insys.BuildingBlocks.Application;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.Party.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Insys.Party.Infrastructure;

public sealed class DueDiligenceJobOptions
{
    public bool JobEnabled { get; set; } = true;

    public TimeSpan JobInterval { get; set; } = TimeSpan.FromHours(24);
}

/// <summary>
/// The PARTY_DUE_DILIGENCE_JOBS job (ADR-026 JOB function, OQ-125): daily, per tenant that runs it, expires parties past
/// review and screens again those screened against an older list version or not for a month. As a service identity.
/// </summary>
public sealed class DueDiligenceJobs(IServiceScopeFactory scopes, ITenantDirectory tenants, IFunctionAvailability availability, ILogger<DueDiligenceJobs> logger)
{
    public const string Function = "PARTY_DUE_DILIGENCE_JOBS";

    public const string ServiceName = "party-due-diligence";

    public async Task<int> Run(CancellationToken cancellationToken)
    {
        var runs = 0;
        foreach (var tenantId in await tenants.Running(availability, Function, logger, cancellationToken))
        {
            runs += await RunForTenant(tenantId, cancellationToken) is not null ? 1 : 0;
        }

        return runs;
    }

    private const int DormancyBatch = 1000;

    public async Task<DueDiligenceJobRun?> RunForTenant(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ExecutionScope>().InitialiseForService(tenantId, ServiceName);
        var result = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new RunDueDiligenceJobs(), cancellationToken);
        await using (var dormancyScope = scopes.CreateAsyncScope())
        {
            // OQ-054: before retention, so a party gone dormant today is looked at today.
            dormancyScope.ServiceProvider.GetRequiredService<ExecutionScope>().InitialiseForService(tenantId, ServiceName);
            var dormancy = await dormancyScope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new ReviewPartyDormancy(DormancyBatch), cancellationToken);
            if (dormancy.IsFailure)
            {
                logger.LogError("Party dormancy review failed for tenant {TenantId}: {Code}", tenantId, dormancy.Error.Code);
            }
        }

        await using (var retentionScope = scopes.CreateAsyncScope())
        {
            // F064: a separate transaction, so a retention failure doesn't lose the due diligence work.
            retentionScope.ServiceProvider.GetRequiredService<ExecutionScope>().InitialiseForService(tenantId, ServiceName);
            var retention = await retentionScope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new RaisePartyRetentionReviews(200), cancellationToken);
            if (retention.IsFailure)
            {
                logger.LogError("Party retention reviews failed for tenant {TenantId}: {Code}", tenantId, retention.Error.Code);
            }
        }

        if (result.IsFailure)
        {
            logger.LogError("Due diligence jobs failed for tenant {TenantId}: {Code}", tenantId, result.Error.Code);
            return null;
        }

        return result.Value;
    }
}

public sealed class DueDiligenceJobsService(DueDiligenceJobs jobs, IOptions<DueDiligenceJobOptions> options, ILogger<DueDiligenceJobsService> logger) : BackgroundService
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
                await jobs.Run(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("Due diligence jobs failed: {ExceptionType}", ex.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
