using Insys.BuildingBlocks.Api;
using Insys.Party.Api.StaticData;
using Insys.Party.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Party.Api;

/// <summary>The Party module. So far it serves its static data only; its aggregates are built in a later phase.</summary>
public sealed class PartyModule : IModule
{
    public string Name => "Party";

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddPartyInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api) => PartyStaticDataEndpoints.MapStaticData(api);
}
