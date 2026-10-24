/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : General
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
using Insys.Billing.Contracts;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Infrastructure;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.ClaimsFramework.Contracts;
using Insys.Documents.Contracts;
using Insys.General.Application;
using Insys.General.Contracts;
using Insys.General.Infrastructure.Persistence;
using Insys.General.Infrastructure.StaticData;
using Insys.Rating.Contracts;
using Insys.Workflow.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Insys.General.Infrastructure;

public static class GeneralInfrastructure
{
    public static IServiceCollection AddGeneralInfrastructure(this IServiceCollection services)
    {
        const string schema = GeneralDbContext.SchemaName;
        services.AddModuleDbContext<GeneralDbContext>(schema, "General");
        services.AddStaticData<GeneralDbContext>(schema, GeneralStaticData.Descriptors);
        services.AddModuleHandlers<GeneralDbContext>(typeof(CreateQuote).Assembly);
        services.AddDomainEventMapper<GeneralEventMapper>(schema);
        services.AddScoped<IGeneralRepository, GeneralRepository>();
        services.AddScoped<IGeneralLists, GeneralLists>();
        services.AddScoped<IGeneralCodes, GeneralCodes>();
        services.AddSingleton<Distribution.Contracts.IPolicyDistribution, GeneralPolicyDistribution>();
        services.AddIntegrationEventHandler<PremiumCalculated, GeneralPolicies, GeneralDbContext>(schema, "PremiumCalculated");
        services.AddIntegrationEventHandler<ApprovalGranted, GeneralPolicies, GeneralDbContext>(schema, "ApprovalGranted");
        services.AddIntegrationEventHandler<ApprovalRejected, GeneralPolicies, GeneralDbContext>(schema, "ApprovalRejected");
        services.AddIntegrationEventHandler<ClaimPaymentCompleted, GeneralPolicies, GeneralDbContext>(schema, "ClaimPaymentCompleted");
        services.AddIntegrationEventHandler<DocumentGenerated, GeneralPolicies, GeneralDbContext>(schema, "DocumentGenerated");
        services.AddIntegrationEventHandler<GeneralEndorsementApproved, GeneralPolicies, GeneralDbContext>(schema, "GeneralEndorsementApproved");
        services.AddIntegrationEventHandler<GeneralPolicyEndorsed, GeneralPolicies, GeneralDbContext>(schema, "GeneralPolicyEndorsed");
        services.AddIntegrationEventHandler<GeneralPolicyIssued, GeneralPolicies, GeneralDbContext>(schema, "GeneralPolicyIssued");
        services.AddIntegrationEventHandler<AssessorAppointed, GeneralPolicies, GeneralDbContext>(schema, "AssessorAppointed");
        services.AddIntegrationEventHandler<InvoiceSettled, GeneralPolicies, GeneralDbContext>(schema, "InvoiceSettled");
        services.AddOptions<GeneralJobOptions>().BindConfiguration("General:Jobs");
        services.AddSingleton<GeneralJobs>();
        if (!InfrastructureRegistration.IsBuildTimeDocumentGeneration)
        {
            services.AddHostedService<GeneralJobsService>();
        }

        return services;
    }
}

public sealed class GeneralJobOptions
{
    public bool Enabled { get; set; } = true;

    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>
/// The daily transitions, one record per transaction, as a service identity: issued policies come into force when
/// their period starts; renewal invitations go out the rule's notice days before a period ends; policies whose rule
/// renews them by themselves are renewed when it ends (GEN-004); policies in force that were not renewed expire after
/// it; issued quotes expire after their validity; and the day's exposure snapshot is refreshed (OQ-066).
/// </summary>
public sealed class GeneralJobs(IServiceScopeFactory scopes, ITenantDirectory tenants, TenantBusinessDates businessDates, ILogger<GeneralJobs> logger)
{
    public const string ServiceName = "general-jobs";

    public async Task<int> Run(CancellationToken cancellationToken)
    {
        var done = 0;
        foreach (var tenantId in await tenants.GetActiveTenantIds(cancellationToken))
        {
            // The tenant's business date (GAP-044, ADR-025).
            var day = businessDates.TodayFor(tenantId);
            DueWork work;
            await using (var scope = Scope(tenantId))
            {
                var found = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Query(new FindDueWork(day, 500), cancellationToken);
                if (found.IsFailure)
                {
                    continue;
                }

                work = found.Value;
            }

            foreach (var id in work.PoliciesToRenew)
            {
                await using var scope = Scope(tenantId);
                var renewed = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new RenewPolicy(id), cancellationToken);
                if (renewed.IsSuccess)
                {
                    done++;
                }
                else
                {
                    logger.LogWarning("General job RenewPolicy on {Id} failed: {Code}", id, renewed.Error.Code);
                }
            }

            foreach (var (id, action) in work.PoliciesToStart.Select(id => (id, DueAction.StartPeriod))
                         .Concat(work.PoliciesToInvite.Select(id => (id, DueAction.InviteRenewal)))
                         .Concat(work.PoliciesToExpire.Select(id => (id, DueAction.ExpirePolicy)))
                         .Concat(work.QuotesToExpire.Select(id => (id, DueAction.ExpireQuote))))
            {
                await using var scope = Scope(tenantId);
                var result = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new RunDue(id, action, day), cancellationToken);
                if (result.IsSuccess)
                {
                    done++;
                }
                else
                {
                    logger.LogWarning("General job {Action} on {Id} failed: {Code}", action, id, result.Error.Code);
                }
            }
        }

        return done;
    }

    /// <summary>The day's exposure snapshot, per tenant.</summary>
    public async Task<int> RefreshExposure(CancellationToken cancellationToken)
    {
        var rows = 0;
        foreach (var tenantId in await tenants.GetActiveTenantIds(cancellationToken))
        {
            await using var scope = Scope(tenantId);
            var refreshed = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new RefreshExposureSnapshot(businessDates.TodayFor(tenantId)), cancellationToken);
            if (refreshed.IsSuccess)
            {
                rows += refreshed.Value;
            }
            else
            {
                logger.LogWarning("Exposure snapshot for tenant {TenantId} failed: {Code}", tenantId, refreshed.Error.Code);
            }
        }

        return rows;
    }

    private AsyncServiceScope Scope(Guid tenantId)
    {
        var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ExecutionScope>().InitialiseForService(tenantId, ServiceName);
        return scope;
    }
}

public sealed class GeneralJobsService(GeneralJobs jobs, IOptions<GeneralJobOptions> options, ILogger<GeneralJobsService> logger) : BackgroundService
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
                await jobs.RefreshExposure(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("General jobs failed: {ExceptionType}", ex.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
