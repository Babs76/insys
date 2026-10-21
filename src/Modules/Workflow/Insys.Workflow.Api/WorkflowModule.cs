using Insys.BuildingBlocks.Api;
using Insys.Workflow.Api.StaticData;
using Insys.Workflow.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Workflow.Api;

/// <summary>The Workflow module. So far it serves its static data only; its aggregates are built in a later phase.</summary>
public sealed class WorkflowModule : IModule
{
    public string Name => "Workflow";

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddWorkflowInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api) => WorkflowStaticDataEndpoints.MapStaticData(api);
}
