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
        return services;
    }
}
