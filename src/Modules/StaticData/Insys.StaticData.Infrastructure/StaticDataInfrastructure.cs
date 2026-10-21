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
        services.AddModuleHandlers<StaticDataDbContext>(typeof(ApproveChange).Assembly);
        services.AddDomainEventMapper<StaticDataEventMapper>(StaticDataDbContext.SchemaName);
        services.AddScoped<IChangeRequestRepository, ChangeRequestRepository>();
        services.AddScoped<IChangeRequestQueries, ChangeRequestQueries>();
        var schema = StaticDataDbContext.SchemaName;
        services.AddIntegrationEventHandler<StaticDataChangeSubmitted, StaticDataGovernancePolicies, StaticDataDbContext>(schema, "Submitted");
        services.AddIntegrationEventHandler<StaticDataVersionActivated, StaticDataGovernancePolicies, StaticDataDbContext>(schema, "Activated");
        services.AddIntegrationEventHandler<StaticDataCodeDeprecated, StaticDataGovernancePolicies, StaticDataDbContext>(schema, "Deprecated");
        services.AddIntegrationEventHandler<StaticDataImportCompleted, StaticDataGovernancePolicies, StaticDataDbContext>(schema, "ImportCompleted");
        services.AddIntegrationEventHandler<StaticDataImportRejected, StaticDataGovernancePolicies, StaticDataDbContext>(schema, "ImportRejected");
        return services;
    }
}
