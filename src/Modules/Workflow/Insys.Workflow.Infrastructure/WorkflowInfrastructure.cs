using Insys.BuildingBlocks.Infrastructure;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Workflow.Infrastructure.Persistence;
using Insys.Workflow.Infrastructure.StaticData;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Workflow.Infrastructure;

public static class WorkflowInfrastructure
{
    public static IServiceCollection AddWorkflowInfrastructure(this IServiceCollection services)
    {
        services.AddModuleDbContext<WorkflowDbContext>(WorkflowDbContext.SchemaName, "Workflow");
        services.AddStaticData<WorkflowDbContext>(WorkflowDbContext.SchemaName, WorkflowStaticData.Descriptors);
        return services;
    }
}
