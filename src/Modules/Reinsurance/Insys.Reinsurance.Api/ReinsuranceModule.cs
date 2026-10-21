using Insys.BuildingBlocks.Api;
using Insys.Reinsurance.Api.StaticData;
using Insys.Reinsurance.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Reinsurance.Api;

/// <summary>The Reinsurance module. So far it serves its static data only; its aggregates are built in a later phase.</summary>
public sealed class ReinsuranceModule : IModule
{
    public string Name => "Reinsurance";

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddReinsuranceInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api) => ReinsuranceStaticDataEndpoints.MapStaticData(api);
}
