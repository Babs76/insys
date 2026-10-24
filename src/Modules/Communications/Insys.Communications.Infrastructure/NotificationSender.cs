/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Communications
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
using Insys.Communications.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Insys.Communications.Infrastructure;

/// <summary>
/// Sends the due notifications of each active tenant (communications §5), each in its own scope and transaction, so one
/// failure stops only that notification. Background work runs for active tenants only.
/// </summary>
public sealed class NotificationSender(IServiceScopeFactory scopes, ITenantDirectory tenants, IOptions<CommunicationOptions> options, ILogger<NotificationSender> logger)
{
    public const string ServiceName = "communication-sender";

    public async Task<int> RunOnce(CancellationToken cancellationToken)
    {
        var sent = 0;
        foreach (var tenantId in await tenants.GetActiveTenantIds(cancellationToken))
        {
            try
            {
                sent += await RunForTenant(tenantId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("Notification sending failed for tenant {TenantId}: {ExceptionType}", tenantId, ex.GetType().Name);
            }
        }

        return sent;
    }

    /// <summary>One pass for a tenant: how many notifications were handled.</summary>
    public async Task<int> RunForTenant(Guid tenantId, CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> due;
        await using (var scope = Scope(tenantId))
        {
            var found = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Query(new FindDueNotifications(options.Value.BatchSize), cancellationToken);
            due = found.IsSuccess ? found.Value : [];
        }

        var handled = 0;
        foreach (var id in due)
        {
            await using var scope = Scope(tenantId);
            var result = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new SendNotification(id), cancellationToken);
            if (result.IsFailure)
            {
                logger.LogWarning("Notification {NotificationId} not sent: {Code}", id, result.Error.Code);
            }
            else
            {
                handled++;
            }
        }

        return handled;
    }

    private AsyncServiceScope Scope(Guid tenantId)
    {
        var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ExecutionScope>().InitialiseForService(tenantId, ServiceName);
        return scope;
    }
}

public sealed class NotificationSenderService(NotificationSender sender, IOptions<CommunicationOptions> options, ILogger<NotificationSenderService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.JobEnabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(options.Value.PollInterval);
        do
        {
            try
            {
                await sender.RunOnce(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("Notification sending pass failed: {ExceptionType}", ex.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
