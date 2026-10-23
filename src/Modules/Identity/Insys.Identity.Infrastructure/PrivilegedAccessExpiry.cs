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
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.Identity.Application.Access;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Insys.Identity.Infrastructure;

/// <summary>
/// Expires approved privileged access when its window ends (transition "Window ends", TEN-007), one tenant and one
/// grant at a time, as a service identity. The grant is also inactive by its window alone, so the job only records it.
/// </summary>
public sealed class PrivilegedAccessExpiry(IServiceScopeFactory scopes, ITenantDirectory tenants, IClock clock, ILogger<PrivilegedAccessExpiry> logger)
{
    public const string ServiceName = "privileged-access-expiry";

    public async Task<int> ExpireDue(CancellationToken cancellationToken)
    {
        var expired = 0;
        foreach (var tenantId in await tenants.GetActiveTenantIds(cancellationToken))
        {
            IReadOnlyList<Guid> due;
            await using (var scope = scopes.CreateAsyncScope())
            {
                scope.ServiceProvider.GetRequiredService<ExecutionScope>().InitialiseForService(tenantId, ServiceName);
                due = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Query(new DueGrants(clock.UtcNow), cancellationToken) is { IsSuccess: true } r
                    ? r.Value
                    : [];
            }

            foreach (var grantId in due)
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<ExecutionScope>().InitialiseForService(tenantId, ServiceName);
                var result = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new ExpireAccess(grantId), cancellationToken);
                if (result.IsSuccess)
                {
                    expired++;
                }
                else
                {
                    logger.LogWarning("Could not expire grant {GrantId}: {Code}", grantId, result.Error.Code);
                }
            }
        }

        return expired;
    }
}

public sealed class PrivilegedAccessExpiryService(PrivilegedAccessExpiry expiry, IOptions<PrivilegedAccessOptions> options, ILogger<PrivilegedAccessExpiryService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.ExpiryEnabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(options.Value.ExpiryInterval);
        do
        {
            try
            {
                await expiry.ExpireDue(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("Privileged access expiry failed: {ExceptionType}", ex.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
