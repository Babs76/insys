using Insys.BuildingBlocks.Api;
using Insys.Life.Api.StaticData;
using Insys.Life.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Life.Api;

/// <summary>The Life module. So far it serves its static data only; its aggregates are built in a later phase.</summary>
public sealed class LifeModule : IModule
{
    public string Name => "Life";

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddLifeInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api) => LifeStaticDataEndpoints.MapStaticData(api);
}
