using Insys.BuildingBlocks.Infrastructure;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Finance.Infrastructure.Persistence;
using Insys.Finance.Infrastructure.StaticData;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Finance.Infrastructure;

public static class FinanceInfrastructure
{
    public static IServiceCollection AddFinanceInfrastructure(this IServiceCollection services)
    {
        services.AddModuleDbContext<FinanceDbContext>(FinanceDbContext.SchemaName, "Finance");
        services.AddStaticData<FinanceDbContext>(FinanceDbContext.SchemaName, FinanceStaticData.Descriptors);
        return services;
    }
}
