using Insys.BuildingBlocks.Api;
using Insys.ProductFactory.Api.StaticData;
using Insys.ProductFactory.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.ProductFactory.Api;

/// <summary>The ProductFactory module. So far it serves its static data only; its aggregates are built in a later phase.</summary>
public sealed class ProductFactoryModule : IModule
{
    public string Name => "ProductFactory";

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddProductFactoryInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api) => ProductFactoryStaticDataEndpoints.MapStaticData(api);
}
