using Insys.BuildingBlocks.Infrastructure;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Audit.Infrastructure.Persistence;
using Insys.Audit.Infrastructure.StaticData;
using Insys.Audit.Application;
using Insys.Audit.Infrastructure.Partitions;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Contracts.Auditing;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Audit.Infrastructure;

public static class AuditInfrastructure
{
    public static IServiceCollection AddAuditInfrastructure(this IServiceCollection services)
    {
        services.AddModuleDbContext<AuditDbContext>(AuditDbContext.SchemaName, "Audit");
        services.AddStaticData<AuditDbContext>(AuditDbContext.SchemaName, AuditStaticData.Descriptors);
        services.AddModuleHandlers<AuditDbContext>(typeof(ListAuditEvents).Assembly);
        services.AddScoped<IAuditEventQueries, AuditEventQueries>();
        services.AddScoped<IAuditSink, AuditSink>();
        services.AddIntegrationEventHandler<SensitiveOperationRecorded, SensitiveOperationRecordedHandler, AuditDbContext>(AuditDbContext.SchemaName);

        services.AddOptions<PartitionOptions>().BindConfiguration("Audit:Partitions");
        services.AddSingleton<PartitionMaintainer>();
        if (!InfrastructureRegistration.IsBuildTimeDocumentGeneration)
        {
            services.AddHostedService<PartitionMaintenanceService>();
        }

        services.AddHealthChecks().AddCheck<PartitionHorizonHealthCheck>("partitions", tags: ["ready"]);
        return services;
    }
}
