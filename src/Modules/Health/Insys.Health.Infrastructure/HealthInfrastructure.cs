/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Health
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
using Insys.BuildingBlocks.Infrastructure;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Health.Application;
using Insys.Health.Contracts;
using Insys.Health.Infrastructure.Persistence;
using Insys.Health.Infrastructure.StaticData;
using Insys.Payments.Contracts;
using Insys.Workflow.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Insys.Health.Infrastructure;

public static class HealthInfrastructure
{
    public static IServiceCollection AddHealthInfrastructure(this IServiceCollection services)
    {
        const string schema = HealthDbContext.SchemaName;
        services.AddModuleDbContext<HealthDbContext>(schema, "Health");
        services.AddSingleton<BuildingBlocks.Infrastructure.StaticData.IStaticRecordValidator, Persistence.HealthFraudIndicatorValidator>();
        services.AddStaticData<HealthDbContext>(schema, HealthStaticData.Descriptors);
        services.AddModuleHandlers<HealthDbContext>(typeof(EnrolHealthMember).Assembly);
        services.AddDomainEventMapper<HealthEventMapper>(schema);
        services.AddScoped<IHealthRepository, HealthRepository>();
        services.AddScoped<IHealthPortalQueries, HealthPortalQueries>();
        services.AddSingleton<BuildingBlocks.Infrastructure.StaticData.IStaticSetGuard, PlanProductsGuard>();
        services.AddSingleton<BuildingBlocks.Contracts.IBusinessRecordScopes, HealthRemittanceScopes>();
        services.AddSingleton<Contracts.IProviderDirectory, ProviderDirectory>();
        services.AddSingleton<Workflow.Contracts.IApprovalScopeSource, PaymentRunApprovalScopes>();
        services.AddHealthChecks().AddCheck<PlanProductsHealthCheck>("health-plan-products");
        services.AddScoped<IHealthCodes, HealthCodes>();
        services.AddScoped<IHealthLists, HealthLists>();
        services.AddScoped<BenefitContexts>();
        services.AddIntegrationEventHandler<PreauthorisationApproved, HealthEventPolicies, HealthDbContext>(schema, "PreauthorisationApproved");
        services.AddIntegrationEventHandler<PreauthorisationReservationsChanged, HealthEventPolicies, HealthDbContext>(schema, "ReservationsChanged");
        services.AddIntegrationEventHandler<MedicalClaimAdjudicated, HealthEventPolicies, HealthDbContext>(schema, "MedicalClaimAdjudicated");
        services.AddIntegrationEventHandler<ApprovalGranted, HealthEventPolicies, HealthDbContext>(schema, "ApprovalGranted");
        services.AddIntegrationEventHandler<ApprovalRejected, HealthEventPolicies, HealthDbContext>(schema, "ApprovalRejected");
        services.AddIntegrationEventHandler<ProviderPaymentRunReleased, HealthEventPolicies, HealthDbContext>(schema, "ProviderPaymentRunReleased");
        services.AddIntegrationEventHandler<PaymentSucceeded, HealthEventPolicies, HealthDbContext>(schema, "PaymentSucceeded");
        services.AddIntegrationEventHandler<Documents.Contracts.DocumentGenerated, HealthEventPolicies, HealthDbContext>(schema, "DocumentGenerated");
        services.AddOptions<HealthJobOptions>().BindConfiguration("Health:Jobs");
        services.AddSingleton<HealthJobs>();
        if (!InfrastructureRegistration.IsBuildTimeDocumentGeneration)
        {
            services.AddHostedService<HealthJobsService>();
        }

        services.AddSingleton<BuildingBlocks.Application.Context.IPortalRecordRelations, HealthPortalRelations>(); // external-user access §2
        // Onboarding readiness DOMAIN_CODES: once the tenant sets HEALTH up (its numbering scheme), its own reference data is in force too.
        BuildingBlocks.Infrastructure.Readiness.ReadinessRegistration.AddSqlReadiness(services, Persistence.HealthDbContext.SchemaName, ["DOMAIN_REFERENCE_DATA"], async (context, connection, transaction, ct) =>
        {
            var missing = new List<string>();
            if (await BuildingBlocks.Infrastructure.Readiness.ReadinessSql.Count(connection, transaction, $"SELECT count(*) FROM health.cfg_member_numbering_schemes WHERE tenant_id = @t AND {BuildingBlocks.Infrastructure.Readiness.ReadinessSql.InForce}", ct,
                    ("t", context.TenantId), ("inForce", BuildingBlocks.Infrastructure.StaticData.StaticLifecycle.Configuration.InForce), ("day", context.Day)) > 0)
            {
            if (await BuildingBlocks.Infrastructure.Readiness.ReadinessSql.Count(connection, transaction, $"SELECT count(*) FROM health.cfg_plans_and_options WHERE tenant_id = @t AND {BuildingBlocks.Infrastructure.Readiness.ReadinessSql.InForce}", ct,
                    ("t", context.TenantId), ("inForce", BuildingBlocks.Infrastructure.StaticData.StaticLifecycle.Configuration.InForce), ("day", context.Day)) == 0)
            {
                missing.Add("health.cfg_plans_and_options");
            }
            }

            return [BuildingBlocks.Application.ReadinessResult.Of("DOMAIN_REFERENCE_DATA", "DOMAIN_CODES",
                "Each domain the tenant sets up has its own reference data: Life claim and benefit types, General cancellation reasons, Health plans", missing)];
        });
        return services;
    }
}

public sealed class HealthJobOptions
{
    public bool Enabled { get; set; } = true;

    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>
/// The daily transitions, one record per transaction, as a service identity: approved authorisations past their
/// validity expire and release their reservation (HLT-005); memberships and schemes start.
/// </summary>
public sealed class HealthJobs(IServiceScopeFactory scopes, ITenantDirectory tenants, TenantBusinessDates businessDates, ILogger<HealthJobs> logger)
{
    public const string ServiceName = "health-jobs";

    public async Task<int> Run(CancellationToken cancellationToken)
    {
        var done = 0;
        foreach (var tenantId in await tenants.GetActiveTenantIds(cancellationToken))
        {
            // The tenant's business date (GAP-044, ADR-025).
            var day = businessDates.TodayFor(tenantId);
            await using (var scope = Scope(tenantId))
            {
                var assigned = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new AssignPlanProducts(day), cancellationToken);
                done += assigned.IsSuccess ? assigned.Value : 0;
            }

            HealthDueWork work;
            await using (var scope = Scope(tenantId))
            {
                var found = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Query(new FindHealthDueWork(day, 500), cancellationToken);
                if (found.IsFailure)
                {
                    continue;
                }

                work = found.Value;
            }

            foreach (var (id, action) in work.AuthorisationsToExpire.Select(id => (id, HealthDueAction.ExpireAuthorisation))
                         .Concat(work.MembershipsToActivate.Select(id => (id, HealthDueAction.ActivateMembership)))
                         .Concat(work.SchemesToActivate.Select(id => (id, HealthDueAction.ActivateScheme))))
            {
                await using var scope = Scope(tenantId);
                var result = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new RunHealthDue(id, action, day), cancellationToken);
                if (result.IsSuccess)
                {
                    done++;
                }
                else if (result.Error.Code != BuildingBlocks.Domain.ErrorCodes.InvalidStateTransition)
                {
                    logger.LogWarning("Health job {Action} on {Id} failed: {Code}", action, id, result.Error.Code);
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

public sealed class HealthJobsService(HealthJobs jobs, IOptions<HealthJobOptions> options, ILogger<HealthJobsService> logger) : BackgroundService
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
                logger.LogError("Health jobs failed: {ExceptionType}", ex.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
