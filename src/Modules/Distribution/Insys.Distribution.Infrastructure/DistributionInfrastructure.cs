using Insys.Billing.Contracts;
using Insys.BuildingBlocks.Contracts.StaticData;
using Insys.BuildingBlocks.Infrastructure;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Distribution.Application;
using Insys.Distribution.Contracts;
using Insys.Distribution.Infrastructure.Persistence;
using Insys.Distribution.Infrastructure.StaticData;
using Insys.General.Contracts;
using Insys.Life.Contracts;
using Insys.Payments.Contracts;
using Insys.Workflow.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Distribution.Infrastructure;

public static class DistributionInfrastructure
{
    public static IServiceCollection AddDistributionInfrastructure(this IServiceCollection services)
    {
        const string schema = DistributionDbContext.SchemaName;
        services.AddModuleDbContext<DistributionDbContext>(schema, "Distribution");
        services.AddStaticData<DistributionDbContext>(schema, DistributionStaticData.Descriptors);
        services.AddModuleHandlers<DistributionDbContext>(typeof(PrepareStatement).Assembly);
        services.AddDomainEventMapper<DistributionEventMapper>(schema);
        services.AddScoped<IDistributionRepository, DistributionRepository>();
        services.AddScoped<IDistributionCodes, DistributionCodes>();
        services.AddSingleton<IIntermediaries, Intermediaries>();
        services.AddSingleton<IStaticChangeGuard, IntermediaryLifecycleGuard>();
        services.AddIntegrationEventHandler<InvoiceIssued, CommissionPolicies, DistributionDbContext>(schema, "InvoiceIssued");
        services.AddIntegrationEventHandler<InvoiceSettled, CommissionPolicies, DistributionDbContext>(schema, "InvoiceSettled");
        services.AddIntegrationEventHandler<GeneralPolicyCancelled, CommissionPolicies, DistributionDbContext>(schema, "GeneralPolicyCancelled");
        services.AddIntegrationEventHandler<LifePolicyLapsed, CommissionPolicies, DistributionDbContext>(schema, "LifePolicyLapsed");
        services.AddIntegrationEventHandler<ApprovalGranted, CommissionPolicies, DistributionDbContext>(schema, "ApprovalGranted");
        services.AddIntegrationEventHandler<ApprovalRejected, CommissionPolicies, DistributionDbContext>(schema, "ApprovalRejected");
        services.AddIntegrationEventHandler<PaymentSucceeded, CommissionPolicies, DistributionDbContext>(schema, "PaymentSucceeded");
        services.AddIntegrationEventHandler<StaticDataChangeApproved, IntermediaryStatusPolicy, DistributionDbContext>(schema, "IntermediaryStatus");
        return services;
    }
}
