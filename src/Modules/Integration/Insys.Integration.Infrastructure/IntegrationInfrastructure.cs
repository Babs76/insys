using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Infrastructure;
using Insys.Integration.Application.DeadLetters;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Integration.Infrastructure.Persistence;
using Insys.Integration.Infrastructure.StaticData;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Integration.Infrastructure;

public static class IntegrationInfrastructure
{
    public static IServiceCollection AddIntegrationInfrastructure(this IServiceCollection services)
    {
        services.AddModuleDbContext<IntegrationDbContext>(IntegrationDbContext.SchemaName, "Integration");
        services.AddStaticData<IntegrationDbContext>(IntegrationDbContext.SchemaName, IntegrationStaticData.Descriptors);
        services.AddModuleHandlers<IntegrationDbContext>(typeof(DeadLetterMessage).Assembly);
        services.AddScoped<IDeadLetterRepository, DeadLetterRepository>();
        services.AddScoped<IDeadLetterQueries, DeadLetterQueries>();
        services.AddScoped<IDeadLetterSink, DeadLetterSink>();
        return services;
    }
}
