using Insys.BuildingBlocks.Infrastructure;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Documents.Infrastructure.Persistence;
using Insys.Documents.Infrastructure.StaticData;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Documents.Infrastructure;

public static class DocumentsInfrastructure
{
    public static IServiceCollection AddDocumentsInfrastructure(this IServiceCollection services)
    {
        services.AddModuleDbContext<DocumentsDbContext>(DocumentsDbContext.SchemaName, "Documents");
        services.AddStaticData<DocumentsDbContext>(DocumentsDbContext.SchemaName, DocumentsStaticData.Descriptors);
        return services;
    }
}
