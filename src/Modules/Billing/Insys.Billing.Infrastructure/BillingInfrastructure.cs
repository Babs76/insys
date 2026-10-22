using Insys.Billing.Application;
using Insys.Billing.Contracts;
using Insys.Billing.Infrastructure.Persistence;
using Insys.Billing.Infrastructure.StaticData;
using Insys.BuildingBlocks.Infrastructure;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.General.Contracts;
using Insys.Payments.Contracts;
using Insys.Workflow.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Billing.Infrastructure;

public static class BillingInfrastructure
{
    public static IServiceCollection AddBillingInfrastructure(this IServiceCollection services)
    {
        const string schema = BillingDbContext.SchemaName;
        services.AddModuleDbContext<BillingDbContext>(schema, "Billing");
        services.AddStaticData<BillingDbContext>(schema, BillingStaticData.Descriptors);
        services.AddModuleHandlers<BillingDbContext>(typeof(OpenAccount).Assembly);
        services.AddDomainEventMapper<BillingEventMapper>(schema);
        services.AddScoped<IBillingRepository, BillingRepository>();
        services.AddScoped<IBillingCodes, BillingCodes>();
        services.AddSingleton<IAllocationPlanner, AllocationPlanner>();
        services.AddIntegrationEventHandler<PaymentAllocated, BillingPolicies, BillingDbContext>(schema, "PaymentAllocated");
        services.AddIntegrationEventHandler<PaymentReversed, BillingPolicies, BillingDbContext>(schema, "PaymentReversed");
        services.AddIntegrationEventHandler<PaymentSucceeded, BillingPolicies, BillingDbContext>(schema, "PaymentSucceeded");
        services.AddIntegrationEventHandler<ApprovalGranted, BillingPolicies, BillingDbContext>(schema, "ApprovalGranted");
        services.AddIntegrationEventHandler<ApprovalRejected, BillingPolicies, BillingDbContext>(schema, "ApprovalRejected");
        services.AddIntegrationEventHandler<GeneralPolicyIssued, GeneralPolicyBilling, BillingDbContext>(schema, "GeneralPolicyIssued");
        services.AddIntegrationEventHandler<GeneralPolicyEndorsed, GeneralPolicyBilling, BillingDbContext>(schema, "GeneralPolicyEndorsed");
        services.AddIntegrationEventHandler<GeneralPolicyCancelled, GeneralPolicyBilling, BillingDbContext>(schema, "GeneralPolicyCancelled");
        services.AddOptions<BillingJobOptions>().BindConfiguration("Billing:Jobs");
        services.AddSingleton<BillingJobs>();
        if (!InfrastructureRegistration.IsBuildTimeDocumentGeneration)
        {
            services.AddHostedService<BillingJobsService>();
        }

        return services;
    }
}
