using Insys.BuildingBlocks.Api;
using Insys.Rating.Api.StaticData;
using Insys.Rating.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Rating.Api;

/// <summary>The Rating module. So far it serves its static data only; its aggregates are built in a later phase.</summary>
public sealed class RatingModule : IModule
{
    public string Name => "Rating";

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddRatingInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api) => RatingStaticDataEndpoints.MapStaticData(api);
}
