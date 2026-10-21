using Insys.BuildingBlocks.Infrastructure;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Billing.Infrastructure.Persistence;
using Insys.Billing.Infrastructure.StaticData;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Billing.Infrastructure;

public static class BillingInfrastructure
{
    public static IServiceCollection AddBillingInfrastructure(this IServiceCollection services)
    {
        services.AddModuleDbContext<BillingDbContext>(BillingDbContext.SchemaName, "Billing");
        services.AddStaticData<BillingDbContext>(BillingDbContext.SchemaName, BillingStaticData.Descriptors);
        return services;
    }
}
