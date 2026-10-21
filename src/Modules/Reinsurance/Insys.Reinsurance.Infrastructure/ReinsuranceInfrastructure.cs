using Insys.BuildingBlocks.Infrastructure;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Reinsurance.Infrastructure.Persistence;
using Insys.Reinsurance.Infrastructure.StaticData;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Reinsurance.Infrastructure;

public static class ReinsuranceInfrastructure
{
    public static IServiceCollection AddReinsuranceInfrastructure(this IServiceCollection services)
    {
        services.AddModuleDbContext<ReinsuranceDbContext>(ReinsuranceDbContext.SchemaName, "Reinsurance");
        services.AddStaticData<ReinsuranceDbContext>(ReinsuranceDbContext.SchemaName, ReinsuranceStaticData.Descriptors);
        return services;
    }
}
