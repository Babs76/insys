using Insys.BuildingBlocks.Api;
using Insys.Billing.Api.StaticData;
using Insys.Billing.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Billing.Api;

/// <summary>The Billing module. So far it serves its static data only; its aggregates are built in a later phase.</summary>
public sealed class BillingModule : IModule
{
    public string Name => "Billing";

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddBillingInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api) => BillingStaticDataEndpoints.MapStaticData(api);
}
