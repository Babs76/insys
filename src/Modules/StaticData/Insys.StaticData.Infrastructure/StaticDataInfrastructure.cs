/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : StaticData
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
using Insys.StaticData.Infrastructure.Persistence;
using Insys.StaticData.Infrastructure.StaticData;
using Insys.BuildingBlocks.Contracts.StaticData;
using Insys.StaticData.Application.Changes;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.StaticData.Infrastructure;

public static class StaticDataInfrastructure
{
    public static IServiceCollection AddStaticDataInfrastructure(this IServiceCollection services)
    {
        services.AddModuleDbContext<StaticDataDbContext>(StaticDataDbContext.SchemaName, "StaticData");
        services.AddStaticData<StaticDataDbContext>(StaticDataDbContext.SchemaName, StaticDataStaticData.Descriptors);
        services.AddModuleHandlers<StaticDataDbContext>(typeof(ListChangeRequests).Assembly);
        services.AddDomainEventMapper<StaticDataEventMapper>(StaticDataDbContext.SchemaName);
        services.AddScoped<IChangeRequestRepository, ChangeRequestRepository>();
        services.AddScoped<IChangeRequestQueries, ChangeRequestQueries>();
        services.AddSingleton<Contracts.IStaticDataChangeLookup, StaticDataChangeLookup>();
        services.AddSingleton<Contracts.IHolidayCalendar, HolidayCalendar>();
        services.AddSingleton<Contracts.ILanguageRegister, LanguageRegister>();
        var schema = StaticDataDbContext.SchemaName;
        services.AddIntegrationEventHandler<StaticDataChangeSubmitted, StaticDataGovernancePolicies, StaticDataDbContext>(schema, "Submitted");
        services.AddIntegrationEventHandler<Workflow.Contracts.ApprovalGranted, StaticDataGovernancePolicies, StaticDataDbContext>(schema, "ApprovalGranted");
        services.AddIntegrationEventHandler<Workflow.Contracts.ApprovalRejected, StaticDataGovernancePolicies, StaticDataDbContext>(schema, "ApprovalRejected");
        services.AddIntegrationEventHandler<StaticDataVersionActivated, StaticDataGovernancePolicies, StaticDataDbContext>(schema, "Activated");
        services.AddIntegrationEventHandler<StaticDataCodeDeprecated, StaticDataGovernancePolicies, StaticDataDbContext>(schema, "Deprecated");
        services.AddIntegrationEventHandler<StaticDataImportCompleted, StaticDataGovernancePolicies, StaticDataDbContext>(schema, "ImportCompleted");
        services.AddIntegrationEventHandler<StaticDataImportRejected, StaticDataGovernancePolicies, StaticDataDbContext>(schema, "ImportRejected");
        // Onboarding readiness (addenda/onboarding-readiness.md §2): the configuration this module owns.
        BuildingBlocks.Infrastructure.Readiness.ReadinessRegistration.AddStaticReadiness(services, Persistence.StaticDataDbContext.SchemaName, StaticDataStaticData.Descriptors,
            new BuildingBlocks.Infrastructure.Readiness.StaticCheck("PLATFORM_REFERENCE_DATA", "GLOBAL_REFERENCE_DATA", "refdata.ref_currencies", "Global currencies, countries, languages and time zones", null, GlobalRows: true),
            new BuildingBlocks.Infrastructure.Readiness.StaticCheck("PLATFORM_REFERENCE_DATA", "GLOBAL_REFERENCE_DATA", "refdata.ref_countries", "Global currencies, countries, languages and time zones", null, GlobalRows: true),
            new BuildingBlocks.Infrastructure.Readiness.StaticCheck("PLATFORM_REFERENCE_DATA", "GLOBAL_REFERENCE_DATA", "refdata.ref_languages", "Global currencies, countries, languages and time zones", null, GlobalRows: true),
            new BuildingBlocks.Infrastructure.Readiness.StaticCheck("PLATFORM_REFERENCE_DATA", "GLOBAL_REFERENCE_DATA", "refdata.ref_time_zones", "Global currencies, countries, languages and time zones", null, GlobalRows: true));
        return services;
    }
}
