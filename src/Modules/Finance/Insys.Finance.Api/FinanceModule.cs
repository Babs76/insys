using Insys.BuildingBlocks.Api;
using Insys.Finance.Api.StaticData;
using Insys.Finance.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Finance.Api;

/// <summary>The Finance module. So far it serves its static data only; its aggregates are built in a later phase.</summary>
public sealed class FinanceModule : IModule
{
    public string Name => "Finance";

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddFinanceInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api) => FinanceStaticDataEndpoints.MapStaticData(api);
}
