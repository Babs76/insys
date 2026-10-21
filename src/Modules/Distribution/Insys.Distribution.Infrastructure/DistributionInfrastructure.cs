using Insys.BuildingBlocks.Infrastructure;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Distribution.Infrastructure.Persistence;
using Insys.Distribution.Infrastructure.StaticData;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Distribution.Infrastructure;

public static class DistributionInfrastructure
{
    public static IServiceCollection AddDistributionInfrastructure(this IServiceCollection services)
    {
        services.AddModuleDbContext<DistributionDbContext>(DistributionDbContext.SchemaName, "Distribution");
        services.AddStaticData<DistributionDbContext>(DistributionDbContext.SchemaName, DistributionStaticData.Descriptors);
        return services;
    }
}
