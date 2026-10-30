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
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Workflow.Infrastructure;

public static class WorkflowInfrastructure
{
    public static IServiceCollection AddWorkflowInfrastructure(this IServiceCollection services)
    {
        const string schema = WorkflowDbContext.SchemaName;
        services.AddModuleDbContext<WorkflowDbContext>(schema, "Workflow");
        // D-34: a legal hold on a case itself, placed only within the caller's scope (F103).
        services.AddLegalHoldTargets(schema, new Dictionary<string, string>
        {
            ["CASE"] = "SELECT product_code, org_node_code FROM workflow.service_case WHERE id = @id",
        });
        services.AddStaticData<WorkflowDbContext>(schema, WorkflowStaticData.Descriptors);
        services.AddModuleHandlers<WorkflowDbContext>(typeof(StartProcess).Assembly);
        services.AddDomainEventMapper<WorkflowEventMapper>(schema);
        services.AddDomainEventMapper<CaseEventMapper>(schema);

        services.AddOptions<ApprovalOptions>().BindConfiguration("Workflow:Approvals");
        services.AddOptions<WorkflowOptions>().BindConfiguration("Workflow");
        services.AddScoped<IApprovalRepository, ApprovalRepository>();
        services.AddScoped<IApprovalQueries, ApprovalQueries>();
        services.AddScoped<IProcessRepository, ProcessRepository>();
        services.AddScoped<IProcessQueries, ProcessQueries>();
        services.AddScoped<WorkflowConfiguration>();
        services.AddScoped<IWorkflowConfiguration>(sp => sp.GetRequiredService<WorkflowConfiguration>());
        services.AddScoped<IAuthorityReader>(sp => sp.GetRequiredService<WorkflowConfiguration>());
        services.AddScoped<IApprovalExpiry>(sp => sp.GetRequiredService<WorkflowConfiguration>());
        services.AddScoped<RequesterFollowUp>();
        services.AddScoped<Contracts.IApprovalHistory, ApprovalHistory>();
        services.AddSingleton<BuildingBlocks.Infrastructure.StaticData.IStaticRecordValidator, ApprovalExpiryValidator>();
        services.AddScoped<ApprovalDecision>();
        services.AddScoped<TaskEscalation>();
        services.AddScoped<Contracts.IBusinessTime, BusinessTime>();
        AddCases(services);

        services.AddIntegrationEventHandler<ApprovalRequested, RequestApprovalPolicy, WorkflowDbContext>(schema);
        services.AddIntegrationEventHandler<ApprovalWithdrawn, WithdrawApprovalPolicy, WorkflowDbContext>(schema);
        services.AddIntegrationEventHandler<Contracts.SlaBreached, EscalateOnSlaBreach, WorkflowDbContext>(schema);
        services.AddIntegrationEventHandler<Integration.Contracts.ParkedDeliveryEscalated, OpenParkedDeliveryTask, WorkflowDbContext>(schema);
        services.AddIntegrationEventHandler<Integration.Contracts.DeliveryParked, ApprovalNotAppliedPolicy, WorkflowDbContext>(schema);
        services.AddIntegrationEventHandler<Integration.Contracts.ParkedDeliveryResolved, ApprovalAppliedPolicy, WorkflowDbContext>(schema);
        services.AddIntegrationEventHandler<ClaimsFramework.Contracts.FraudIndicatorsNotEvaluated, OpenConfigurationReviewTask, WorkflowDbContext>(schema);
        services.AddIntegrationEventHandler<ClaimsFramework.Contracts.ClaimPaymentReleaseHeld, OpenHeldClaimPaymentTask, WorkflowDbContext>(schema);
        services.AddIntegrationEventHandler<Party.Contracts.PartyMatchReviewRequested, OpenPartyMatchReviewTask, WorkflowDbContext>(schema);
        services.AddIntegrationEventHandler<General.Contracts.AggregationLimitApproached, OpenAggregationReviewTask, WorkflowDbContext>(schema);
        services.AddIntegrationEventHandler<General.Contracts.GeneralQuoteReferred, OpenGeneralReferralTask, WorkflowDbContext>(schema);
        services.AddIntegrationEventHandler<General.Contracts.GeneralQuoteReferralDecided, CloseGeneralReferralTask, WorkflowDbContext>(schema);
        services.AddIntegrationEventHandler<Identity.Contracts.AdministratorGranted, TenantAdministratorAuthority, WorkflowDbContext>(schema);
        services.AddIntegrationEventHandler<Party.Contracts.PartyMatchResolved, ClosePartyMatchReviewTask, WorkflowDbContext>(schema);
        services.AddIntegrationEventHandler<Party.Contracts.DueDiligenceReferred, DueDiligenceTasks, WorkflowDbContext>(schema, "DueDiligenceReferred");
        services.AddIntegrationEventHandler<Party.Contracts.DueDiligenceBlocked, DueDiligenceTasks, WorkflowDbContext>(schema, "DueDiligenceBlocked");
        services.AddIntegrationEventHandler<Party.Contracts.DueDiligenceExpired, DueDiligenceTasks, WorkflowDbContext>(schema, "DueDiligenceExpired");
        services.AddIntegrationEventHandler<Party.Contracts.DueDiligenceChanged, DueDiligenceTasks, WorkflowDbContext>(schema, "DueDiligenceChanged");
        services.AddIntegrationEventHandler<Health.Contracts.MembershipTerminationDue, HealthArrearsTerminationTasks, WorkflowDbContext>(schema, "Due");
        services.AddIntegrationEventHandler<General.Contracts.GeneralRenewalReferred, GeneralRenewalReviewTasks, WorkflowDbContext>(schema, "Referred");
        services.AddIntegrationEventHandler<General.Contracts.GeneralRenewalNotEligible, GeneralRenewalReviewTasks, WorkflowDbContext>(schema, "NotEligible");
        services.AddIntegrationEventHandler<General.Contracts.GeneralRenewalDecided, GeneralRenewalReviewTasks, WorkflowDbContext>(schema, "Decided");
        services.AddIntegrationEventHandler<General.Contracts.GeneralRetentionCaseOpened, GeneralRetentionTasks, WorkflowDbContext>(schema, "Opened"); // F026
        services.AddIntegrationEventHandler<General.Contracts.GeneralRetentionCaseClosed, GeneralRetentionTasks, WorkflowDbContext>(schema, "Closed");
        services.AddIntegrationEventHandler<Health.Contracts.MembershipArrearsCleared, HealthArrearsTerminationTasks, WorkflowDbContext>(schema, "Cleared");
        services.AddIntegrationEventHandler<Health.Contracts.ProviderMeasureBreached, ProviderMeasureReviewTasks, WorkflowDbContext>(schema); // F028
        services.AddIntegrationEventHandler<Health.Contracts.CareActivityOverdue, CareActivityFollowUpTasks, WorkflowDbContext>(schema); // F037
        services.AddSingleton<Contracts.IProviderCaseCounts, Persistence.ProviderCaseCounts>(); // F029

        services.AddSingleton<WorkflowJobs>();
        services.AddSingleton<CaseTimelineJobs>();
        if (!InfrastructureRegistration.IsBuildTimeDocumentGeneration)
        {
            services.AddHostedService<WorkflowJobsService>();
            services.AddHostedService<CaseTimelineJobsService>();
        }

        // Onboarding readiness (addenda/onboarding-readiness.md §2): the configuration this module owns.
        BuildingBlocks.Infrastructure.Readiness.ReadinessRegistration.AddStaticReadiness(services, Persistence.WorkflowDbContext.SchemaName, WorkflowStaticData.Descriptors,
            new BuildingBlocks.Infrastructure.Readiness.StaticCheck("SHARED_OPERATIONAL_SETUP", "BUSINESS_CALENDAR", "workflow.ref_business_calendars", "A business calendar, the tenant's or global", null, GlobalRows: true),
            new BuildingBlocks.Infrastructure.Readiness.StaticCheck("SHARED_OPERATIONAL_SETUP", "WORK_QUEUES", "workflow.cfg_task_types_and_work_queues", "Task types and work queues"));
        services.AddSingleton<BuildingBlocks.Application.IReadinessCheck, ApprovalAuthorityReadiness>(); // every approval type the tenant can raise is decidable
        return services;
    }

    /// <summary>Cases (docs/specs/addenda/cases.md): stores, configuration, redress approvals and payments, notices, figures and readiness.</summary>
    private static void AddCases(IServiceCollection services)
    {
        const string schema = WorkflowDbContext.SchemaName;
        services.AddScoped<ICaseRepository, CaseRepository>();
        services.AddScoped<CaseConfiguration>();
        services.AddScoped<ICaseConfiguration>(sp => sp.GetRequiredService<CaseConfiguration>());
        services.AddScoped<ICaseQueries, CaseQueries>();
        services.AddScoped<ICasePortalQueries, CasePortalQueries>();
        services.AddScoped<CaseTimes>();
        services.AddScoped<CaseAccess>();
        services.AddScoped<CaseOpening>();
        services.AddScoped<CaseNoticeTexts>();
        services.AddScoped<Contracts.ICaseNotices, CaseNotices>();
        services.AddScoped<Contracts.IClaimAppealRoutes, ClaimAppealRoutes>(); // OQ-101, OQ-102
        services.AddIntegrationEventHandler<Contracts.ApprovalGranted, CaseRedressPolicies, WorkflowDbContext>(schema);
        services.AddIntegrationEventHandler<Contracts.ApprovalRejected, CaseRedressPolicies, WorkflowDbContext>(schema);
        services.AddIntegrationEventHandler<Payments.Contracts.PaymentSucceeded, CaseRedressPolicies, WorkflowDbContext>(schema);

        // OQ-104: redress is approved by a second person, from the case's screen.
        ApprovalKinds.Declare(Contracts.CaseApprovals.RedressBusinessRefType, "SERVICE_COMPLAINTS", Contracts.CaseApprovals.RedressApprovalType);
        BuildingBlocks.Application.DashboardFigureCodes.Declare(CaseFigures.All);

        // OQ-110: the case checks join SHARED_OPERATIONAL_SETUP (§10). The notification types are Communications' check.
        BuildingBlocks.Infrastructure.Readiness.ReadinessRegistration.AddSqlReadiness(services, schema, ["SHARED_OPERATIONAL_SETUP"], CaseReadiness.Check);
    }
}
