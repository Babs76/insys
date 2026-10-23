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
