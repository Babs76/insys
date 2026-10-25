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
        services.AddSingleton<TenantLocaleDefaults>();
        services.AddSingleton<ITenantBaseCurrency>(sp => sp.GetRequiredService<TenantLocaleDefaults>());
        services.AddSingleton<ITenantTimeZone>(sp => sp.GetRequiredService<TenantLocaleDefaults>());
        services.AddSingleton<BuildingBlocks.Application.Context.ITenantTimeZoneSource>(sp => sp.GetRequiredService<TenantLocaleDefaults>());
        services.AddHealthChecks().AddCheck<TenantTimeZonesHealthCheck>("tenant-time-zones");
        services.AddSingleton<IOrganisationStructure, OrganisationStructureQuery>();
        services.AddSingleton<ITenantProfiles, TenantProfiles>();
        services.AddSingleton<ITenantFunctionSettings, TenantFunctionSettingsQuery>();
        services.AddMemoryCache();
        services.AddSingleton<IStaticRecordValidator, LocaleDefaultsValidator>();
        services.AddSingleton<TenantLanguageSettings>();
        services.AddSingleton<Insys.BuildingBlocks.Application.Localisation.ILanguageSettings>(sp => sp.GetRequiredService<TenantLanguageSettings>());
        services.AddIntegrationEventHandler<Insys.BuildingBlocks.Contracts.StaticData.StaticDataVersionActivated, LocaleCachePolicy, TenancyDbContext>(
            TenancyDbContext.SchemaName, "LocaleDefaultsActivated");
        services.AddIntegrationEventHandler<Identity.Contracts.TenantAdministratorProvisioned, OnboardingAccessPolicy, TenancyDbContext>(TenancyDbContext.SchemaName);
        // Onboarding readiness (addenda/onboarding-readiness.md §2): the configuration this module owns.
        BuildingBlocks.Infrastructure.Readiness.ReadinessRegistration.AddStaticReadiness(services, Persistence.TenancyDbContext.SchemaName, TenancyStaticData.Descriptors,
            new BuildingBlocks.Infrastructure.Readiness.StaticCheck("TENANT_REGISTRATION", "LOCALE_DEFAULTS", "tenant.cfg_locale_defaults", "Locale defaults: base currency, languages and default language", "base_currency IS NOT NULL AND default_language IS NOT NULL AND cardinality(languages) > 0"),
            new BuildingBlocks.Infrastructure.Readiness.StaticCheck("ORGANISATION_AND_ACCESS", "ORGANISATION_STRUCTURE", "tenant.mst_organisation_structure", "A root legal entity in the organisation structure (F103)", "type = 'LEGAL_ENTITY' AND parent IS NULL"),
            new BuildingBlocks.Infrastructure.Readiness.StaticCheck("PRODUCT_FACTORY", "PRODUCT_PUBLISHED", "tenant.cfg_tenant_product_availability", "A product published and made available to the tenant", "enabled"));
        services.AddScoped<Application.Tenants.OnboardingReadiness>();
        services.AddSingleton<Application.Tenants.ITenantLocale, Persistence.TenantLocale>();
        BuildingBlocks.Infrastructure.Readiness.ReadinessRegistration.AddSqlReadiness(services, Persistence.TenancyDbContext.SchemaName, ["FINANCIAL_FOUNDATIONS", "TENANT_REGISTRATION"], async (context, connection, transaction, ct) =>
        {
            // TIME_ZONE: given at registration, or in the locale defaults (ADR-025); BASE_CURRENCY: the locale defaults' base currency.
            var zones = await BuildingBlocks.Infrastructure.Readiness.ReadinessSql.Count(connection, transaction, $"""
                SELECT (SELECT count(*) FROM tenant.tenant WHERE id = @t AND time_zone IS NOT NULL)
                     + (SELECT count(*) FROM tenant.cfg_locale_defaults WHERE tenant_id = @t AND time_zone IS NOT NULL AND {BuildingBlocks.Infrastructure.Readiness.ReadinessSql.InForce})
                """, ct, ("t", context.TenantId), ("inForce", BuildingBlocks.Infrastructure.StaticData.StaticLifecycle.Configuration.InForce), ("day", context.Day));
            return
            [
                BuildingBlocks.Application.ReadinessResult.Of("TENANT_REGISTRATION", "TIME_ZONE", "A time zone, given at registration or in locale defaults (ADR-025)",
                    zones > 0 ? [] : ["tenant.tenant time_zone, or tenant.cfg_locale_defaults time_zone"]),
                BuildingBlocks.Application.ReadinessResult.Of("FINANCIAL_FOUNDATIONS", "BASE_CURRENCY", "The locale defaults' base currency",
                    context.BaseCurrency is { Length: > 0 } ? [] : ["tenant.cfg_locale_defaults base_currency"]),
            ];
        });
        return services;
    }
}
