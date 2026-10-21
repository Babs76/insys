using Insys.BuildingBlocks.Infrastructure;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.ClaimsFramework.Infrastructure.Persistence;
using Insys.ClaimsFramework.Infrastructure.StaticData;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.ClaimsFramework.Infrastructure;

public static class ClaimsFrameworkInfrastructure
{
    public static IServiceCollection AddClaimsFrameworkInfrastructure(this IServiceCollection services)
    {
        services.AddModuleDbContext<ClaimsFrameworkDbContext>(ClaimsFrameworkDbContext.SchemaName, "ClaimsFramework");
        services.AddStaticData<ClaimsFrameworkDbContext>(ClaimsFrameworkDbContext.SchemaName, ClaimsFrameworkStaticData.Descriptors);
        return services;
    }
}
