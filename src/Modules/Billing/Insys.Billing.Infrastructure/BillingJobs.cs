/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Billing
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
using Insys.Billing.Application;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Insys.Billing.Infrastructure;

public sealed class BillingJobOptions
{
    public bool Enabled { get; set; } = true;

    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>Issues invoices for instalments due and ages accounts for arrears, one account per transaction, as a service identity.</summary>
public sealed class BillingJobs(IServiceScopeFactory scopes, ITenantDirectory tenants, TenantBusinessDates businessDates, ILogger<BillingJobs> logger)
{
    public const string ServiceName = "billing-jobs";

    public async Task<(int Invoiced, int Aged)> Run(CancellationToken cancellationToken)
    {
        var (invoiced, aged) = (0, 0);
        foreach (var tenantId in await tenants.GetActiveTenantIds(cancellationToken))
        {
            // The tenant's business date (GAP-044, ADR-025).
            var today = businessDates.TodayFor(tenantId);
            foreach (var accountId in await Query(tenantId, new AccountsDue(today, ForInvoicing: true), cancellationToken))
            {
                invoiced += await Send(tenantId, new IssueDueInvoices(accountId, today), cancellationToken) ? 1 : 0;
            }

            foreach (var accountId in await Query(tenantId, new AccountsDue(today, ForInvoicing: false), cancellationToken))
            {
                aged += await Send(tenantId, new AgeAccount(accountId, today), cancellationToken) ? 1 : 0;
            }
        }

        return (invoiced, aged);
    }

    private async Task<IReadOnlyList<Guid>> Query(Guid tenantId, AccountsDue query, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ExecutionScope>().InitialiseForService(tenantId, ServiceName);
        return await scope.ServiceProvider.GetRequiredService<IDispatcher>().Query(query, cancellationToken) is { IsSuccess: true } r ? r.Value : [];
    }

    private async Task<bool> Send<T>(Guid tenantId, ICommand<T> command, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ExecutionScope>().InitialiseForService(tenantId, ServiceName);
        var result = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(command, cancellationToken);
        if (result.IsFailure)
        {
            logger.LogWarning("{Command} failed: {Code}", command.GetType().Name, result.Error.Code);
        }

        return result.IsSuccess;
    }
}

public sealed class BillingJobsService(BillingJobs jobs, IOptions<BillingJobOptions> options, ILogger<BillingJobsService> logger) : BackgroundService
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
                await jobs.Run(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("Billing jobs failed: {ExceptionType}", ex.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
