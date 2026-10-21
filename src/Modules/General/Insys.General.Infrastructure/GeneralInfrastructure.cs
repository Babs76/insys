using Insys.BuildingBlocks.Infrastructure;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.General.Infrastructure.Persistence;
using Insys.General.Infrastructure.StaticData;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.General.Infrastructure;

public static class GeneralInfrastructure
{
    public static IServiceCollection AddGeneralInfrastructure(this IServiceCollection services)
    {
        services.AddModuleDbContext<GeneralDbContext>(GeneralDbContext.SchemaName, "General");
        services.AddStaticData<GeneralDbContext>(GeneralDbContext.SchemaName, GeneralStaticData.Descriptors);
        return services;
    }
}
