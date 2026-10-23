/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Tenancy
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
using Insys.Tenancy.Infrastructure.Persistence;
using Insys.Tenancy.Infrastructure.StaticData;
using Insys.BuildingBlocks.Application.Context;
using Insys.Tenancy.Application.Tenants;
using Insys.Tenancy.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Tenancy.Infrastructure;

public static class TenancyInfrastructure
{
    public static IServiceCollection AddTenancyInfrastructure(this IServiceCollection services)
    {
        services.AddModuleDbContext<TenancyDbContext>(TenancyDbContext.SchemaName, "Tenancy");
        services.AddStaticData<TenancyDbContext>(TenancyDbContext.SchemaName, TenancyStaticData.Descriptors);
        services.AddModuleHandlers<TenancyDbContext>(typeof(RegisterTenant).Assembly);
        services.AddDomainEventMapper<TenancyEventMapper>(TenancyDbContext.SchemaName);
        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<ITenantQueries, TenantQueries>();
        services.AddSingleton<ITenantDirectory, TenantDirectory>();
        services.AddSingleton<ITenantStatusQuery, TenantStatusQuery>();
        services.AddSingleton<ITenantBaseCurrency, TenantBaseCurrency>();
        services.AddSingleton<ITenantProfiles, TenantProfiles>();
        services.AddMemoryCache();
        services.AddSingleton<IStaticRecordValidator, LocaleDefaultsValidator>();
        services.AddSingleton<TenantLanguageSettings>();
        services.AddSingleton<Insys.BuildingBlocks.Application.Localisation.ILanguageSettings>(sp => sp.GetRequiredService<TenantLanguageSettings>());
        services.AddIntegrationEventHandler<Insys.BuildingBlocks.Contracts.StaticData.StaticDataVersionActivated, LocaleCachePolicy, TenancyDbContext>(
            TenancyDbContext.SchemaName, "LocaleDefaultsActivated");
        return services;
    }
}
