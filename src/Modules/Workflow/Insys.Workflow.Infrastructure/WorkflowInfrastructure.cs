/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Workflow
 * CONFIDENTIALITY AND INTELLECTUAL PROPERTY NOTICE
 * This source code and its associated documentation contain confidential and
 * proprietary information belonging to OpenFactor Technology Group or its
 * applicable intellectual property rights holder.
 * This material is provided solely for authorised development, maintenance,
 * operation or evaluation purposes, as applicable. No person may reproduce,
 * modify, distribute, disclose, publish or otherwise use this material except
 * as expressly permitted by an applicable written agreement or licence.
 * No ownership rights or licence to any intellectual property are granted
 * by the inclusion of this notice, except as expressly provided in an
 * applicable written agreement or licence.
 * Third-party software, open-source components and customer-owned materials
 * remain subject to their respective ownership rights and applicable licences
 * or agreements.
 * UNAUTHORISED ACCESS, USE, COPYING OR DISCLOSURE IS PROHIBITED.
 * ============================================================================
 */
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
        services.AddIntegrationEventHandler<ApprovalWithdrawn, WithdrawApprovalPolicy, WorkflowDbContext>(schema);
        services.AddIntegrationEventHandler<Contracts.SlaBreached, EscalateOnSlaBreach, WorkflowDbContext>(schema);

        services.AddSingleton<WorkflowJobs>();
        if (!InfrastructureRegistration.IsBuildTimeDocumentGeneration)
        {
            services.AddHostedService<WorkflowJobsService>();
        }

        return services;
    }
}
