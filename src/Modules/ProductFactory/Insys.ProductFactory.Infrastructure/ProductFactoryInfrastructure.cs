/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : ProductFactory
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
using Insys.ProductFactory.Application;
using Insys.ProductFactory.Contracts;
using Insys.ProductFactory.Infrastructure.Persistence;
using Insys.ProductFactory.Infrastructure.StaticData;
using Insys.Workflow.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.ProductFactory.Infrastructure;

public static class ProductFactoryInfrastructure
{
    public static IServiceCollection AddProductFactoryInfrastructure(this IServiceCollection services)
    {
        // Its approval kinds and the screens that open their records (BR-005).
        BuildingBlocks.Contracts.Approvals.ApprovalKinds.Declare(Insys.ProductFactory.Contracts.ProductApprovals.BusinessRefType, "PRODUCTS", Insys.ProductFactory.Contracts.ProductApprovals.ApprovalType);
        const string schema = ProductFactoryDbContext.SchemaName;
        services.AddModuleDbContext<ProductFactoryDbContext>(schema, "ProductFactory");
        services.AddStaticData<ProductFactoryDbContext>(schema, ProductFactoryStaticData.Descriptors);
        services.AddModuleHandlers<ProductFactoryDbContext>(typeof(RegisterProduct).Assembly);
        services.AddScoped<IProductVersionRepository, ProductVersionRepository>();
        services.AddScoped<IProductList, ProductList>();
        services.AddScoped<ISimulationRuns, SimulationRunStore>();
        services.AddScoped<IProductCatalogue, ProductCatalogue>();
        services.AddSingleton<SellableProducts>();
        services.AddSingleton<ISellableProducts>(sp => sp.GetRequiredService<SellableProducts>());
        services.AddSingleton<IPublishedProducts>(sp => sp.GetRequiredService<SellableProducts>());
        services.AddSingleton<BuildingBlocks.Application.IReadinessCheck, ProductReadiness>();
        services.AddSingleton<IProductDomains, ProductDomains>();
        services.AddSingleton<IExclusionCodes, ExclusionCodesQuery>();
        services.AddIntegrationEventHandler<ApprovalGranted, ProductPolicies, ProductFactoryDbContext>(schema, "ApprovalGranted");
        services.AddIntegrationEventHandler<ApprovalRejected, ProductPolicies, ProductFactoryDbContext>(schema, "ApprovalRejected");
        services.AddIntegrationEventHandler<StaticDataVersionActivated, ProductPolicies, ProductFactoryDbContext>(schema, "StaticDataActivated");
        services.AddOptions<ProductJobOptions>().BindConfiguration("ProductFactory:Jobs");
        services.AddSingleton<ProductJobs>();
        if (!InfrastructureRegistration.IsBuildTimeDocumentGeneration)
        {
            services.AddHostedService<ProductJobsService>();
        }

        return services;
    }
}
