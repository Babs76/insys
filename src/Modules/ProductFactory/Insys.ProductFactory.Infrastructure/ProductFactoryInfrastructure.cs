using Insys.BuildingBlocks.Contracts.StaticData;
using Insys.BuildingBlocks.Infrastructure;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.ProductFactory.Application;
using Insys.ProductFactory.Infrastructure.Persistence;
using Insys.ProductFactory.Infrastructure.StaticData;
using Insys.Workflow.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.ProductFactory.Infrastructure;

public static class ProductFactoryInfrastructure
{
    public static IServiceCollection AddProductFactoryInfrastructure(this IServiceCollection services)
    {
        const string schema = ProductFactoryDbContext.SchemaName;
        services.AddModuleDbContext<ProductFactoryDbContext>(schema, "ProductFactory");
        services.AddStaticData<ProductFactoryDbContext>(schema, ProductFactoryStaticData.Descriptors);
        services.AddModuleHandlers<ProductFactoryDbContext>(typeof(RegisterProduct).Assembly);
        services.AddScoped<IProductVersionRepository, ProductVersionRepository>();
        services.AddScoped<ISimulationRuns, SimulationRunStore>();
        services.AddScoped<IProductCatalogue, ProductCatalogue>();
        services.AddIntegrationEventHandler<ApprovalGranted, ProductPolicies, ProductFactoryDbContext>(schema, "ApprovalGranted");
        services.AddIntegrationEventHandler<ApprovalRejected, ProductPolicies, ProductFactoryDbContext>(schema, "ApprovalRejected");
        services.AddIntegrationEventHandler<StaticDataVersionActivated, ProductPolicies, ProductFactoryDbContext>(schema, "StaticDataActivated");
        services.AddOptions<ProductJobOptions>().BindConfiguration("ProductFactory:Jobs");
        services.AddSingleton<ProductJobs>();
        if (!InfrastructureRegistration.IsBuildTimeDocumentGeneration)
        {
            services.AddHostedService<ProductJobsService>();
        }

        return services;
    }
}
