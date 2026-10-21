using Insys.BuildingBlocks.Api;
using Insys.ClaimsFramework.Api.StaticData;
using Insys.ClaimsFramework.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.ClaimsFramework.Api;

/// <summary>The ClaimsFramework module. So far it serves its static data only; its aggregates are built in a later phase.</summary>
public sealed class ClaimsFrameworkModule : IModule
{
    public string Name => "ClaimsFramework";

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddClaimsFrameworkInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api) => ClaimsFrameworkStaticDataEndpoints.MapStaticData(api);
}
