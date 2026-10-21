using Insys.BuildingBlocks.Infrastructure;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Health.Infrastructure.Persistence;
using Insys.Health.Infrastructure.StaticData;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Health.Infrastructure;

public static class HealthInfrastructure
{
    public static IServiceCollection AddHealthInfrastructure(this IServiceCollection services)
    {
        services.AddModuleDbContext<HealthDbContext>(HealthDbContext.SchemaName, "Health");
        services.AddStaticData<HealthDbContext>(HealthDbContext.SchemaName, HealthStaticData.Descriptors);
        return services;
    }
}
