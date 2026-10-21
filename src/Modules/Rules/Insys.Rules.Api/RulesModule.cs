using Insys.BuildingBlocks.Api;
using Insys.Rules.Api.StaticData;
using Insys.Rules.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Rules.Api;

/// <summary>The Rules module. So far it serves its static data only; its aggregates are built in a later phase.</summary>
public sealed class RulesModule : IModule
{
    public string Name => "Rules";

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddRulesInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api) => RulesStaticDataEndpoints.MapStaticData(api);
}
