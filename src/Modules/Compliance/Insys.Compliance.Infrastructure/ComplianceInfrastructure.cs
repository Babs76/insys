using Insys.BuildingBlocks.Infrastructure;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Compliance.Infrastructure.Persistence;
using Insys.Compliance.Infrastructure.StaticData;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Compliance.Infrastructure;

public static class ComplianceInfrastructure
{
    public static IServiceCollection AddComplianceInfrastructure(this IServiceCollection services)
    {
        services.AddModuleDbContext<ComplianceDbContext>(ComplianceDbContext.SchemaName, "Compliance");
        services.AddStaticData<ComplianceDbContext>(ComplianceDbContext.SchemaName, ComplianceStaticData.Descriptors);
        return services;
    }
}
