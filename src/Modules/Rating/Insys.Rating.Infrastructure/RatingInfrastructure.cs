/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Rating
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
using Insys.BuildingBlocks.Contracts.StaticData;
using Insys.BuildingBlocks.Infrastructure;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.ProductFactory.Contracts;
using Insys.Rating.Application;
using Insys.Rating.Contracts;
using Insys.Rating.Infrastructure.Persistence;
using Insys.Rating.Infrastructure.StaticData;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Rating.Infrastructure;

public static class RatingInfrastructure
{
    public static IServiceCollection AddRatingInfrastructure(this IServiceCollection services)
    {
        const string schema = RatingDbContext.SchemaName;
        services.AddModuleDbContext<RatingDbContext>(schema, "Rating");
        services.AddStaticData<RatingDbContext>(schema, RatingStaticData.Descriptors);
        services.AddModuleHandlers<RatingDbContext>(typeof(CalculatePremium).Assembly);
        services.AddDomainEventMapper<RatingEventMapper>(schema);
        services.AddMemoryCache();
        services.AddSingleton<RatingConfigurationCache>();
        services.AddScoped<IRatingConfigurationReader, RatingConfigurationReader>();
        services.AddScoped<IRatingTestCases, RatingTestCases>();
        services.AddSingleton<IStaticRecordValidator, RatingTestCaseValidator>();
        services.AddScoped<IPremiumCalculationRepository, PremiumCalculationRepository>();
        services.AddScoped<IPremiumCalculationQueries, PremiumCalculationQueries>();
        services.AddScoped<PremiumRater>();
        services.AddScoped<IPremiumSimulator, PremiumSimulator>();
        services.AddIntegrationEventHandler<ProductVersionPublished, RatingCachePolicies, RatingDbContext>(schema, "ProductVersionPublished");
        services.AddIntegrationEventHandler<StaticDataVersionActivated, RatingCachePolicies, RatingDbContext>(schema, "StaticDataActivated");
        return services;
    }
}
