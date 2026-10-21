using Insys.BuildingBlocks.Api;
using Insys.Payments.Api.StaticData;
using Insys.Payments.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Payments.Api;

/// <summary>The Payments module. So far it serves its static data only; its aggregates are built in a later phase.</summary>
public sealed class PaymentsModule : IModule
{
    public string Name => "Payments";

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddPaymentsInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api) => PaymentsStaticDataEndpoints.MapStaticData(api);
}
