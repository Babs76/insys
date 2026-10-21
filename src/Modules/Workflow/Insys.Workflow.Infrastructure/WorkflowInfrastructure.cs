using Insys.BuildingBlocks.Contracts.Approvals;
using Insys.BuildingBlocks.Infrastructure;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Workflow.Application;
using Insys.Workflow.Infrastructure.Persistence;
using Insys.Workflow.Infrastructure.StaticData;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Workflow.Infrastructure;

public static class WorkflowInfrastructure
{
    public static IServiceCollection AddWorkflowInfrastructure(this IServiceCollection services)
    {
        const string schema = WorkflowDbContext.SchemaName;
        services.AddModuleDbContext<WorkflowDbContext>(schema, "Workflow");
        services.AddStaticData<WorkflowDbContext>(schema, WorkflowStaticData.Descriptors);
        services.AddModuleHandlers<WorkflowDbContext>(typeof(StartProcess).Assembly);
        services.AddDomainEventMapper<WorkflowEventMapper>(schema);

        services.AddOptions<ApprovalOptions>().BindConfiguration("Workflow:Approvals");
        services.AddOptions<WorkflowOptions>().BindConfiguration("Workflow");
        services.AddScoped<IApprovalRepository, ApprovalRepository>();
        services.AddScoped<IApprovalQueries, ApprovalQueries>();
        services.AddScoped<IProcessRepository, ProcessRepository>();
        services.AddScoped<IProcessQueries, ProcessQueries>();
        services.AddScoped<WorkflowConfiguration>();
        services.AddScoped<IWorkflowConfiguration>(sp => sp.GetRequiredService<WorkflowConfiguration>());
        services.AddScoped<IAuthorityReader>(sp => sp.GetRequiredService<WorkflowConfiguration>());
        services.AddScoped<ApprovalDecision>();
        services.AddScoped<TaskEscalation>();

        services.AddIntegrationEventHandler<ApprovalRequested, RequestApprovalPolicy, WorkflowDbContext>(schema);
        services.AddIntegrationEventHandler<Contracts.SlaBreached, EscalateOnSlaBreach, WorkflowDbContext>(schema);

        services.AddSingleton<WorkflowJobs>();
        if (!InfrastructureRegistration.IsBuildTimeDocumentGeneration)
        {
            services.AddHostedService<WorkflowJobsService>();
        }

        return services;
    }
}
