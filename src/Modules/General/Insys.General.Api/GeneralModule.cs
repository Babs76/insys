using Insys.BuildingBlocks.Api;
using Insys.General.Api.StaticData;
using Insys.General.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.General.Api;

/// <summary>The General module. So far it serves its static data only; its aggregates are built in a later phase.</summary>
public sealed class GeneralModule : IModule
{
    public string Name => "General";

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddGeneralInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api) => GeneralStaticDataEndpoints.MapStaticData(api);
}
