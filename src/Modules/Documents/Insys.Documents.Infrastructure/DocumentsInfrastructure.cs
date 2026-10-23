/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Documents
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
using Insys.Documents.Application;
using Insys.Documents.Infrastructure.Persistence;
using Insys.Documents.Infrastructure.StaticData;
using Insys.General.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Documents.Infrastructure;

public static class DocumentsInfrastructure
{
    public static IServiceCollection AddDocumentsInfrastructure(this IServiceCollection services)
    {
        services.AddModuleDbContext<DocumentsDbContext>(DocumentsDbContext.SchemaName, "Documents");
        services.AddStaticData<DocumentsDbContext>(DocumentsDbContext.SchemaName, DocumentsStaticData.Descriptors);
        services.AddModuleHandlers<DocumentsDbContext>(typeof(GenerateDocument).Assembly);
        services.AddDomainEventMapper<DocumentsEventMapper>(DocumentsDbContext.SchemaName);
        services.AddScoped<IDocumentRepository, DocumentRepository>();
        services.AddScoped<IDocumentCatalogue, DocumentCatalogue>();
        services.AddOptions<DocumentStorageOptions>().BindConfiguration("Documents:Storage");
        services.AddSingleton<IDocumentStorage, FileSystemDocumentStorage>();
        services.AddSingleton<IDocumentRenderer, PlainTextDocumentRenderer>();
        services.AddSingleton<IDocumentDelivery, StubDocumentDelivery>();
        services.AddScoped<DocumentGenerator>();
        services.AddScoped<DocumentLanguages>();
        services.AddIntegrationEventHandler<GeneralPolicyIssued, GeneralDocumentPolicies, DocumentsDbContext>(DocumentsDbContext.SchemaName, "GeneralPolicyIssued");
        services.AddIntegrationEventHandler<GeneralPolicyEndorsed, GeneralDocumentPolicies, DocumentsDbContext>(DocumentsDbContext.SchemaName, "GeneralPolicyEndorsed");
        services.AddIntegrationEventHandler<GeneralRenewalInvited, GeneralDocumentPolicies, DocumentsDbContext>(DocumentsDbContext.SchemaName, "GeneralRenewalInvited");
        services.AddIntegrationEventHandler<Life.Contracts.LifePolicyIssued, LifeDocumentPolicies, DocumentsDbContext>(DocumentsDbContext.SchemaName, "LifePolicyIssued");
        services.AddIntegrationEventHandler<Health.Contracts.ProviderPaymentRunReleased, HealthDocumentPolicies, DocumentsDbContext>(DocumentsDbContext.SchemaName,
            "ProviderPaymentRunReleased");
        return services;
    }
}
