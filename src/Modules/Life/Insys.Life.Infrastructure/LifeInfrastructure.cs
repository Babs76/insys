/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Life
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
using Insys.Life.Application;
using Insys.Life.Contracts;
using Insys.Life.Infrastructure.Persistence;
using Insys.Life.Infrastructure.StaticData;
using Insys.Rating.Contracts;
using Insys.Rules.Contracts;
using Insys.Workflow.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Insys.Life.Infrastructure;

public static class LifeInfrastructure
{
    public static IServiceCollection AddLifeInfrastructure(this IServiceCollection services)
    {
        const string schema = LifeDbContext.SchemaName;
        services.AddModuleDbContext<LifeDbContext>(schema, "Life");
        services.AddStaticData<LifeDbContext>(schema, LifeStaticData.Descriptors);
        services.AddModuleHandlers<LifeDbContext>(typeof(CaptureProposal).Assembly);
        services.AddDomainEventMapper<LifeEventMapper>(schema);
        services.AddScoped<ILifeRepository, LifeRepository>();
        services.AddScoped<ILifeCodes, LifeCodes>();
        services.AddScoped<ILifeLists, LifeLists>();
        services.AddScoped<ILifePortalQueries, LifePortalQueries>();
        services.AddSingleton<BuildingBlocks.Infrastructure.StaticData.IStaticSetGuard, FreeCoverBandsGuard>();
        services.AddSingleton<Distribution.Contracts.IPolicyDistribution, LifePolicyDistribution>();
        services.AddSingleton<Workflow.Contracts.IApprovalAuthoritySource, ClaimsAuthority>();
        services.AddSingleton<Workflow.Contracts.IApprovalScopeSource, LifeApprovalScopes>();
        services.AddIntegrationEventHandler<PremiumCalculated, LifeEventPolicies, LifeDbContext>(schema, "PremiumCalculated");
        services.AddIntegrationEventHandler<RuleDecisionRecorded, LifeEventPolicies, LifeDbContext>(schema, "RuleDecisionRecorded");
        services.AddIntegrationEventHandler<LifeProposalSubmitted, LifeEventPolicies, LifeDbContext>(schema, "LifeProposalSubmitted");
        services.AddIntegrationEventHandler<LifeUnderwritingCaseOpened, LifeEventPolicies, LifeDbContext>(schema, "LifeUnderwritingCaseOpened");
        services.AddIntegrationEventHandler<LifeUnderwritingDecided, LifeEventPolicies, LifeDbContext>(schema, "LifeUnderwritingDecided");
        services.AddIntegrationEventHandler<LifePolicyIssued, LifeEventPolicies, LifeDbContext>(schema, "LifePolicyIssued");
        services.AddIntegrationEventHandler<ApprovalGranted, LifeEventPolicies, LifeDbContext>(schema, "ApprovalGranted");
        services.AddIntegrationEventHandler<ApprovalRejected, LifeEventPolicies, LifeDbContext>(schema, "ApprovalRejected");
        services.AddIntegrationEventHandler<ArrearsDetected, LifeEventPolicies, LifeDbContext>(schema, "ArrearsDetected");
        services.AddIntegrationEventHandler<InvoiceSettled, LifeEventPolicies, LifeDbContext>(schema, "InvoiceSettled");
        services.AddIntegrationEventHandler<ClaimPaymentCompleted, LifeEventPolicies, LifeDbContext>(schema, "ClaimPaymentCompleted");
        services.AddIntegrationEventHandler<LifeClaimApproved, LifeEventPolicies, LifeDbContext>(schema, "LifeClaimApproved");
        services.AddIntegrationEventHandler<LifeClaimSettled, LifeEventPolicies, LifeDbContext>(schema, "LifeClaimSettled");
        services.AddOptions<LifeJobOptions>().BindConfiguration("Life:Jobs");
        services.AddOptions<LifeClaimOptions>().BindConfiguration("Life:Claims");
        services.AddSingleton<LifeJobs>();
        if (!InfrastructureRegistration.IsBuildTimeDocumentGeneration)
        {
            services.AddHostedService<LifeJobsService>();
        }

        services.AddSingleton<LifePortalRelations>(); // external-user access §2: other modules' portal reads
        services.AddSingleton<BuildingBlocks.Application.Context.IPortalRecordRelations>(sp => sp.GetRequiredService<LifePortalRelations>());
        services.AddSingleton<BuildingBlocks.Application.Context.IBrokeredPolicies>(sp => sp.GetRequiredService<LifePortalRelations>());
        return services;
    }
}

public sealed class LifeJobOptions
{
    public bool Enabled { get; set; } = true;

    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>
/// The daily transitions, one record per transaction, as a service identity: issued policies come into force at
/// inception; policies past grace with enough missed payments lapse (LIF-005); lapsed policies past their
/// reinstatement window terminate; group schemes start.
/// </summary>
public sealed class LifeJobs(IServiceScopeFactory scopes, ITenantDirectory tenants, TenantBusinessDates businessDates, ILogger<LifeJobs> logger)
{
    public const string ServiceName = "life-jobs";

    public async Task<int> Run(CancellationToken cancellationToken)
    {
        var done = 0;
        foreach (var tenantId in await tenants.GetActiveTenantIds(cancellationToken))
        {
            // The tenant's business date (GAP-044, ADR-025).
            var day = businessDates.TodayFor(tenantId);
            LifeDueWork work;
            await using (var scope = Scope(tenantId))
            {
                var found = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Query(new FindLifeDueWork(day, 500), cancellationToken);
                if (found.IsFailure)
                {
                    continue;
                }

                work = found.Value;
            }

            foreach (var (id, action) in work.PoliciesToStart.Select(id => (id, LifeDueAction.StartCover))
                         .Concat(work.PoliciesPastGrace.Select(id => (id, LifeDueAction.Lapse)))
                         .Concat(work.LapsedPolicies.Select(id => (id, LifeDueAction.Terminate)))
                         .Concat(work.SchemesToActivate.Select(id => (id, LifeDueAction.ActivateScheme))))
            {
                await using var scope = Scope(tenantId);
                var result = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new RunLifeDue(id, action, day), cancellationToken);
                if (result.IsSuccess)
                {
                    done++;
                }
                else if (result.Error.Code != BuildingBlocks.Domain.ErrorCodes.InvalidStateTransition)
                {
                    logger.LogWarning("Life job {Action} on {Id} failed: {Code}", action, id, result.Error.Code);
                }
            }
        }

        return done;
    }

    private AsyncServiceScope Scope(Guid tenantId)
    {
        var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ExecutionScope>().InitialiseForService(tenantId, ServiceName);
        return scope;
    }
}

public sealed class LifeJobsService(LifeJobs jobs, IOptions<LifeJobOptions> options, ILogger<LifeJobsService> logger) : BackgroundService
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
                logger.LogError("Life jobs failed: {ExceptionType}", ex.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
