using Insys.BuildingBlocks.Infrastructure;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Party.Infrastructure.Persistence;
using Insys.Party.Infrastructure.StaticData;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Party.Infrastructure;

public static class PartyInfrastructure
{
    public static IServiceCollection AddPartyInfrastructure(this IServiceCollection services)
    {
        services.AddModuleDbContext<PartyDbContext>(PartyDbContext.SchemaName, "Party");
        services.AddStaticData<PartyDbContext>(PartyDbContext.SchemaName, PartyStaticData.Descriptors);
        return services;
    }
}
