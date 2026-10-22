using Insys.Analytics.Application;
using Insys.Analytics.Contracts;
using Insys.Analytics.Infrastructure.Persistence;
using Insys.Analytics.Infrastructure.StaticData;
using Insys.BuildingBlocks.Infrastructure;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.ClaimsFramework.Contracts;
using Insys.General.Contracts;
using Insys.Life.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Analytics.Infrastructure;

public static class AnalyticsInfrastructure
{
    public static IServiceCollection AddAnalyticsInfrastructure(this IServiceCollection services)
    {
        const string schema = AnalyticsDbContext.SchemaName;
        services.AddModuleDbContext<AnalyticsDbContext>(schema, "Analytics");
        services.AddStaticData<AnalyticsDbContext>(schema, AnalyticsStaticData.Descriptors);
        services.AddModuleHandlers<AnalyticsDbContext>(typeof(RebuildProjections).Assembly);
        services.AddScoped<IProjectionStore, ProjectionStore>();
        services.AddSingleton<IProjectionTotals, ProjectionTotals>();
        services.AddIntegrationEventHandler<GeneralPolicyIssued, ProjectionPolicies, AnalyticsDbContext>(schema, "GeneralPolicyIssued");
        services.AddIntegrationEventHandler<GeneralPolicyEndorsed, ProjectionPolicies, AnalyticsDbContext>(schema, "GeneralPolicyEndorsed");
        services.AddIntegrationEventHandler<GeneralPolicyCancelled, ProjectionPolicies, AnalyticsDbContext>(schema, "GeneralPolicyCancelled");
        services.AddIntegrationEventHandler<LifePolicyIssued, ProjectionPolicies, AnalyticsDbContext>(schema, "LifePolicyIssued");
        services.AddIntegrationEventHandler<ClaimNumberAllocated, ProjectionPolicies, AnalyticsDbContext>(schema, "ClaimNumberAllocated");
        services.AddIntegrationEventHandler<ClaimReserveChanged, ProjectionPolicies, AnalyticsDbContext>(schema, "ClaimReserveChanged");
        return services;
    }
}
