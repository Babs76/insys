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
        services.AddScoped<Contracts.IBusinessTime, BusinessTime>();

        services.AddIntegrationEventHandler<ApprovalRequested, RequestApprovalPolicy, WorkflowDbContext>(schema);
        services.AddIntegrationEventHandler<ApprovalWithdrawn, WithdrawApprovalPolicy, WorkflowDbContext>(schema);
        services.AddIntegrationEventHandler<Contracts.SlaBreached, EscalateOnSlaBreach, WorkflowDbContext>(schema);
        services.AddIntegrationEventHandler<Integration.Contracts.ParkedDeliveryEscalated, OpenParkedDeliveryTask, WorkflowDbContext>(schema);
        services.AddIntegrationEventHandler<ClaimsFramework.Contracts.FraudIndicatorsNotEvaluated, OpenConfigurationReviewTask, WorkflowDbContext>(schema);
        services.AddIntegrationEventHandler<ClaimsFramework.Contracts.ClaimPaymentReleaseHeld, OpenHeldClaimPaymentTask, WorkflowDbContext>(schema);
        services.AddIntegrationEventHandler<Party.Contracts.PartyMatchReviewRequested, OpenPartyMatchReviewTask, WorkflowDbContext>(schema);
        services.AddIntegrationEventHandler<Party.Contracts.PartyMatchResolved, ClosePartyMatchReviewTask, WorkflowDbContext>(schema);

        services.AddSingleton<WorkflowJobs>();
        if (!InfrastructureRegistration.IsBuildTimeDocumentGeneration)
        {
            services.AddHostedService<WorkflowJobsService>();
        }

        // Onboarding readiness (addenda/onboarding-readiness.md §2): the configuration this module owns.
        BuildingBlocks.Infrastructure.Readiness.ReadinessRegistration.AddStaticReadiness(services, Persistence.WorkflowDbContext.SchemaName, WorkflowStaticData.Descriptors,
            new BuildingBlocks.Infrastructure.Readiness.StaticCheck("SHARED_OPERATIONAL_SETUP", "BUSINESS_CALENDAR", "workflow.ref_business_calendars", "A business calendar, the tenant's or global", null, GlobalRows: true),
            new BuildingBlocks.Infrastructure.Readiness.StaticCheck("SHARED_OPERATIONAL_SETUP", "WORK_QUEUES", "workflow.cfg_task_types_and_work_queues", "Task types and work queues"));
        BuildingBlocks.Infrastructure.Readiness.ReadinessRegistration.AddSqlReadiness(services, Persistence.WorkflowDbContext.SchemaName, ["SHARED_OPERATIONAL_SETUP"], async (context, connection, transaction, ct) =>
        {
            // The maker-checker approval types in use (onboarding readiness §2): refunds, payout batches, claim payment
            // instructions, endorsements and claim settlements.
            string[] used = ["REFUND", "PAYOUT_RELEASE", "CLAIM_PAYMENT", "ENDORSEMENT", "CLAIM_SETTLEMENT"];
            var authorised = await BuildingBlocks.Infrastructure.Readiness.ReadinessSql.Strings(connection, transaction,
                $"SELECT DISTINCT transaction_type FROM workflow.cfg_delegation_and_approval_authority WHERE tenant_id = @t AND {BuildingBlocks.Infrastructure.Readiness.ReadinessSql.InForce}", ct,
                ("t", context.TenantId), ("inForce", BuildingBlocks.Infrastructure.StaticData.StaticLifecycle.Configuration.InForce), ("day", context.Day));
            return [BuildingBlocks.Application.ReadinessResult.Of("SHARED_OPERATIONAL_SETUP", "APPROVAL_AUTHORITY",
                "Approval authority for every maker-checker approval type in use", used.Except(authorised).ToList())];
        });
        return services;
    }
}
