using Insys.BuildingBlocks.Api;
using Insys.Distribution.Api.StaticData;
using Insys.Distribution.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Distribution.Api;

/// <summary>The Distribution module. So far it serves its static data only; its aggregates are built in a later phase.</summary>
public sealed class DistributionModule : IModule
{
    public string Name => "Distribution";

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddDistributionInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api) => DistributionStaticDataEndpoints.MapStaticData(api);
}
