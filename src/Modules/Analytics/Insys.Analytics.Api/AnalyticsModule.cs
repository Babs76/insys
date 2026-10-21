using Insys.BuildingBlocks.Api;
using Insys.Analytics.Api.StaticData;
using Insys.Analytics.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Analytics.Api;

/// <summary>The Analytics module. So far it serves its static data only; its aggregates are built in a later phase.</summary>
public sealed class AnalyticsModule : IModule
{
    public string Name => "Analytics";

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddAnalyticsInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api) => AnalyticsStaticDataEndpoints.MapStaticData(api);
}
