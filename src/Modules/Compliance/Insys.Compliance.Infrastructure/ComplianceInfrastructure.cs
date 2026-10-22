using Insys.BuildingBlocks.Infrastructure;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Compliance.Application;
using Insys.Compliance.Infrastructure.Persistence;
using Insys.Compliance.Infrastructure.StaticData;
using Insys.Finance.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Compliance.Infrastructure;

public static class ComplianceInfrastructure
{
    public static IServiceCollection AddComplianceInfrastructure(this IServiceCollection services)
    {
        const string schema = ComplianceDbContext.SchemaName;
        services.AddModuleDbContext<ComplianceDbContext>(schema, "Compliance");
        services.AddStaticData<ComplianceDbContext>(schema, ComplianceStaticData.Descriptors);
        services.AddModuleHandlers<ComplianceDbContext>(typeof(PrepareReturn).Assembly);
        services.AddDomainEventMapper<ComplianceEventMapper>(schema);
        services.AddScoped<IComplianceRepository, ComplianceRepository>();
        services.AddScoped<IComplianceCodes, ComplianceCodes>();
        services.AddScoped<ReturnValues>();
        services.AddIntegrationEventHandler<AccountingPeriodClosed, ReturnPolicies, ComplianceDbContext>(schema, "AccountingPeriodClosed");
        return services;
    }
}
