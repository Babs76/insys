using Insys.BuildingBlocks.Infrastructure;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Analytics.Infrastructure.Persistence;
using Insys.Analytics.Infrastructure.StaticData;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Analytics.Infrastructure;

public static class AnalyticsInfrastructure
{
    public static IServiceCollection AddAnalyticsInfrastructure(this IServiceCollection services)
    {
        services.AddModuleDbContext<AnalyticsDbContext>(AnalyticsDbContext.SchemaName, "Analytics");
        services.AddStaticData<AnalyticsDbContext>(AnalyticsDbContext.SchemaName, AnalyticsStaticData.Descriptors);
        return services;
    }
}
