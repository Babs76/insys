using Insys.BuildingBlocks.Api;
using Insys.Health.Api.StaticData;
using Insys.Health.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Health.Api;

/// <summary>The Health module. So far it serves its static data only; its aggregates are built in a later phase.</summary>
public sealed class HealthModule : IModule
{
    public string Name => "Health";

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddHealthInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api) => HealthStaticDataEndpoints.MapStaticData(api);
}
