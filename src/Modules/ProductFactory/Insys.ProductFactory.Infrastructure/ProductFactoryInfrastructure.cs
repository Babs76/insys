using Insys.BuildingBlocks.Infrastructure;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.ProductFactory.Infrastructure.Persistence;
using Insys.ProductFactory.Infrastructure.StaticData;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.ProductFactory.Infrastructure;

public static class ProductFactoryInfrastructure
{
    public static IServiceCollection AddProductFactoryInfrastructure(this IServiceCollection services)
    {
        services.AddModuleDbContext<ProductFactoryDbContext>(ProductFactoryDbContext.SchemaName, "ProductFactory");
        services.AddStaticData<ProductFactoryDbContext>(ProductFactoryDbContext.SchemaName, ProductFactoryStaticData.Descriptors);
        return services;
    }
}
