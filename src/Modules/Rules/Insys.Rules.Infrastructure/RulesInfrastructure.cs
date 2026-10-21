using Insys.BuildingBlocks.Infrastructure;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Rules.Infrastructure.Persistence;
using Insys.Rules.Infrastructure.StaticData;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Rules.Infrastructure;

public static class RulesInfrastructure
{
    public static IServiceCollection AddRulesInfrastructure(this IServiceCollection services)
    {
        services.AddModuleDbContext<RulesDbContext>(RulesDbContext.SchemaName, "Rules");
        services.AddStaticData<RulesDbContext>(RulesDbContext.SchemaName, RulesStaticData.Descriptors);
        return services;
    }
}
