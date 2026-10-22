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
        services.AddStaticData<HealthDbContext>(schema, HealthStaticData.Descriptors);
        services.AddModuleHandlers<HealthDbContext>(typeof(EnrolHealthMember).Assembly);
        services.AddDomainEventMapper<HealthEventMapper>(schema);
        services.AddScoped<IHealthRepository, HealthRepository>();
        services.AddScoped<IHealthCodes, HealthCodes>();
        services.AddScoped<BenefitContexts>();
        services.AddIntegrationEventHandler<PreauthorisationApproved, HealthEventPolicies, HealthDbContext>(schema, "PreauthorisationApproved");
        services.AddIntegrationEventHandler<PreauthorisationReservationsChanged, HealthEventPolicies, HealthDbContext>(schema, "ReservationsChanged");
        services.AddIntegrationEventHandler<MedicalClaimAdjudicated, HealthEventPolicies, HealthDbContext>(schema, "MedicalClaimAdjudicated");
        services.AddIntegrationEventHandler<ApprovalGranted, HealthEventPolicies, HealthDbContext>(schema, "ApprovalGranted");
        services.AddIntegrationEventHandler<ApprovalRejected, HealthEventPolicies, HealthDbContext>(schema, "ApprovalRejected");
        services.AddIntegrationEventHandler<ProviderPaymentRunReleased, HealthEventPolicies, HealthDbContext>(schema, "ProviderPaymentRunReleased");
        services.AddIntegrationEventHandler<PaymentSucceeded, HealthEventPolicies, HealthDbContext>(schema, "PaymentSucceeded");
        services.AddOptions<HealthJobOptions>().BindConfiguration("Health:Jobs");
        services.AddSingleton<HealthJobs>();
        if (!InfrastructureRegistration.IsBuildTimeDocumentGeneration)
        {
            services.AddHostedService<HealthJobsService>();
        }

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
public sealed class HealthJobs(IServiceScopeFactory scopes, ITenantDirectory tenants, IClock clock, ILogger<HealthJobs> logger)
{
    public const string ServiceName = "health-jobs";

    public async Task<int> Run(CancellationToken cancellationToken)
    {
        var done = 0;
        var day = clock.UtcToday;
        foreach (var tenantId in await tenants.GetActiveTenantIds(cancellationToken))
        {
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
