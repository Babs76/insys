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
    public static IServiceCollection AddDocumentsInfrastructure(this IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        services.AddModuleDbContext<DocumentsDbContext>(DocumentsDbContext.SchemaName, "Documents");
        services.AddStaticData<DocumentsDbContext>(DocumentsDbContext.SchemaName, DocumentsStaticData.Descriptors);
        services.AddModuleHandlers<DocumentsDbContext>(typeof(GenerateDocument).Assembly);
        services.AddDomainEventMapper<DocumentsEventMapper>(DocumentsDbContext.SchemaName);
        services.AddScoped<IDocumentRepository, DocumentRepository>();
        services.AddScoped<PortalDocuments>();
        services.AddScoped<IDocumentCatalogue, DocumentCatalogue>();
        services.AddOptions<DocumentStorageOptions>().BindConfiguration("Documents:Storage");
        if (string.Equals(configuration["Documents:Storage:Provider"], "object-store", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IDocumentStorage, ObjectStoreDocumentStorage>();
        }
        else
        {
            services.AddSingleton<IDocumentStorage, FileSystemDocumentStorage>();
        }
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
        BuildingBlocks.Infrastructure.Readiness.ReadinessRegistration.AddSqlReadiness(services, Persistence.DocumentsDbContext.SchemaName, async (context, connection, transaction, ct) =>
        {
            var language = context.DefaultLanguage ?? "";
            var templates = await BuildingBlocks.Infrastructure.Readiness.ReadinessSql.Count(connection, transaction,
                $"SELECT count(*) FROM document.cfg_document_templates WHERE tenant_id = @t AND {BuildingBlocks.Infrastructure.Readiness.ReadinessSql.InForce} AND (@language = '' OR trim(language) = @language)",
                ct, ("t", context.TenantId), ("inForce", BuildingBlocks.Infrastructure.StaticData.StaticLifecycle.Configuration.InForce), ("day", context.Day),
                ("language", language));
            return [BuildingBlocks.Application.ReadinessResult.Of("SHARED_OPERATIONAL_SETUP", "DOCUMENT_TEMPLATES", "A document template in the default language",
                templates > 0 ? [] : [$"document.cfg_document_templates ({(language.Length > 0 ? language : "no default language set")})"])];
        });
        return services;
    }
}
