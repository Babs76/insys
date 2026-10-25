/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Analytics
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
using Insys.Analytics.Application;
using Insys.Analytics.Contracts;
using Insys.Analytics.Infrastructure.Persistence;
using Insys.Analytics.Infrastructure.StaticData;
using Insys.BuildingBlocks.Infrastructure;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.ClaimsFramework.Contracts;
using Insys.General.Contracts;
using Insys.Life.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Analytics.Infrastructure;

public static class AnalyticsInfrastructure
{
    public static IServiceCollection AddAnalyticsInfrastructure(this IServiceCollection services)
    {
        // Its dashboard figure codes, for the OpenAPI enum of DashboardFigure.code.
        BuildingBlocks.Application.DashboardFigureCodes.Declare(Insys.Analytics.Application.AnalyticsFigures.WrittenPremium, Insys.Analytics.Application.AnalyticsFigures.PoliciesWritten, Insys.Analytics.Application.AnalyticsFigures.ClaimsIncurred, Insys.Analytics.Application.AnalyticsFigures.ClaimsReported);
        // ADR-026 §2: this module's event consumers run under its JOB function.
        BuildingBlocks.Application.Context.JobFunctions.ForConsumers(Persistence.AnalyticsDbContext.SchemaName, "ANALYTICS_EVENT_PROCESSING");
        const string schema = AnalyticsDbContext.SchemaName;
        services.AddModuleDbContext<AnalyticsDbContext>(schema, "Analytics");
        services.AddStaticData<AnalyticsDbContext>(schema, AnalyticsStaticData.Descriptors);
        services.AddModuleHandlers<AnalyticsDbContext>(typeof(RebuildProjections).Assembly);
        services.AddScoped<IProjectionStore, ProjectionStore>();
        services.AddSingleton<IProjectionTotals, ProjectionTotals>();
        services.AddSingleton<AnalyticsReleaseTasks>();
        if (!InfrastructureRegistration.IsBuildTimeDocumentGeneration)
        {
            services.AddHostedService<AnalyticsReleaseTasksService>();
        }
        services.AddIntegrationEventHandler<GeneralPolicyIssued, ProjectionPolicies, AnalyticsDbContext>(schema, "GeneralPolicyIssued");
        services.AddIntegrationEventHandler<GeneralPolicyEndorsed, ProjectionPolicies, AnalyticsDbContext>(schema, "GeneralPolicyEndorsed");
        services.AddIntegrationEventHandler<GeneralPolicyCancelled, ProjectionPolicies, AnalyticsDbContext>(schema, "GeneralPolicyCancelled");
        services.AddIntegrationEventHandler<LifePolicyIssued, ProjectionPolicies, AnalyticsDbContext>(schema, "LifePolicyIssued");
        services.AddIntegrationEventHandler<ClaimNumberAllocated, ProjectionPolicies, AnalyticsDbContext>(schema, "ClaimNumberAllocated");
        services.AddIntegrationEventHandler<ClaimReserveChanged, ProjectionPolicies, AnalyticsDbContext>(schema, "ClaimReserveChanged");
        return services;
    }
}
