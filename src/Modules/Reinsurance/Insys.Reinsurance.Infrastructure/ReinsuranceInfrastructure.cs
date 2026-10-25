/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Reinsurance
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
using Insys.BuildingBlocks.Infrastructure;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.General.Contracts;
using Insys.Life.Contracts;
using Insys.Reinsurance.Application;
using Insys.Reinsurance.Infrastructure.Persistence;
using Insys.Reinsurance.Infrastructure.StaticData;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Reinsurance.Infrastructure;

public static class ReinsuranceInfrastructure
{
    public static IServiceCollection AddReinsuranceInfrastructure(this IServiceCollection services)
    {
        // ADR-026 §2: this module's event consumers run under its JOB function.
        BuildingBlocks.Application.Context.JobFunctions.ForConsumers(Persistence.ReinsuranceDbContext.SchemaName, "REINSURANCE_EVENT_PROCESSING");
        const string schema = ReinsuranceDbContext.SchemaName;
        services.AddModuleDbContext<ReinsuranceDbContext>(schema, "Reinsurance");
        services.AddStaticData<ReinsuranceDbContext>(schema, ReinsuranceStaticData.Descriptors);
        services.AddModuleHandlers<ReinsuranceDbContext>(typeof(PrepareBordereau).Assembly);
        services.AddDomainEventMapper<ReinsuranceEventMapper>(schema);
        services.AddScoped<IReinsuranceRepository, ReinsuranceRepository>();
        services.AddScoped<IReinsuranceCodes, ReinsuranceCodes>();
        services.AddIntegrationEventHandler<GeneralPolicyIssued, ReinsurancePolicies, ReinsuranceDbContext>(schema, "GeneralPolicyIssued");
        services.AddIntegrationEventHandler<GeneralPolicyEndorsed, ReinsurancePolicies, ReinsuranceDbContext>(schema, "GeneralPolicyEndorsed");
        services.AddIntegrationEventHandler<LifePolicyIssued, ReinsurancePolicies, ReinsuranceDbContext>(schema, "LifePolicyIssued");
        services.AddIntegrationEventHandler<GeneralClaimSettled, ReinsurancePolicies, ReinsuranceDbContext>(schema, "GeneralClaimSettled");
        services.AddIntegrationEventHandler<LifeClaimSettled, ReinsurancePolicies, ReinsuranceDbContext>(schema, "LifeClaimSettled");
        // Onboarding readiness (addenda/onboarding-readiness.md §2): the configuration this module owns.
        BuildingBlocks.Infrastructure.Readiness.ReadinessRegistration.AddStaticReadiness(services, Persistence.ReinsuranceDbContext.SchemaName, ReinsuranceStaticData.Descriptors,
            new BuildingBlocks.Infrastructure.Readiness.StaticCheck("REINSURANCE", "TREATIES", "reinsurance.cfg_treaty_definitions", "Treaties and cession rules, or no reinsurance recorded with a reason"),
            new BuildingBlocks.Infrastructure.Readiness.StaticCheck("REINSURANCE", "TREATIES", "reinsurance.cfg_cession_rules", "Treaties and cession rules, or no reinsurance recorded with a reason"));
        return services;
    }
}
