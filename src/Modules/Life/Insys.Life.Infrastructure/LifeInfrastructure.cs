using Insys.BuildingBlocks.Infrastructure;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Life.Infrastructure.Persistence;
using Insys.Life.Infrastructure.StaticData;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Life.Infrastructure;

public static class LifeInfrastructure
{
    public static IServiceCollection AddLifeInfrastructure(this IServiceCollection services)
    {
        services.AddModuleDbContext<LifeDbContext>(LifeDbContext.SchemaName, "Life");
        services.AddStaticData<LifeDbContext>(LifeDbContext.SchemaName, LifeStaticData.Descriptors);
        return services;
    }
}
