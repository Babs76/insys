using Insys.BuildingBlocks.Api;
using Insys.Compliance.Api.StaticData;
using Insys.Compliance.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Compliance.Api;

/// <summary>The Compliance module. So far it serves its static data only; its aggregates are built in a later phase.</summary>
public sealed class ComplianceModule : IModule
{
    public string Name => "Compliance";

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddComplianceInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api) => ComplianceStaticDataEndpoints.MapStaticData(api);
}
