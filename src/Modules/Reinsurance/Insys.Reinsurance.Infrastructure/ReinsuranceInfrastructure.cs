using Insys.BuildingBlocks.Infrastructure;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.General.Contracts;
using Insys.Life.Contracts;
using Insys.Reinsurance.Application;
using Insys.Reinsurance.Infrastructure.Persistence;
using Insys.Reinsurance.Infrastructure.StaticData;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Reinsurance.Infrastructure;

public static class ReinsuranceInfrastructure
{
    public static IServiceCollection AddReinsuranceInfrastructure(this IServiceCollection services)
    {
        const string schema = ReinsuranceDbContext.SchemaName;
        services.AddModuleDbContext<ReinsuranceDbContext>(schema, "Reinsurance");
        services.AddStaticData<ReinsuranceDbContext>(schema, ReinsuranceStaticData.Descriptors);
        services.AddModuleHandlers<ReinsuranceDbContext>(typeof(PrepareBordereau).Assembly);
        services.AddDomainEventMapper<ReinsuranceEventMapper>(schema);
        services.AddScoped<IReinsuranceRepository, ReinsuranceRepository>();
        services.AddScoped<IReinsuranceCodes, ReinsuranceCodes>();
        services.AddIntegrationEventHandler<GeneralPolicyIssued, ReinsurancePolicies, ReinsuranceDbContext>(schema, "GeneralPolicyIssued");
        services.AddIntegrationEventHandler<GeneralPolicyEndorsed, ReinsurancePolicies, ReinsuranceDbContext>(schema, "GeneralPolicyEndorsed");
        services.AddIntegrationEventHandler<LifePolicyIssued, ReinsurancePolicies, ReinsuranceDbContext>(schema, "LifePolicyIssued");
        services.AddIntegrationEventHandler<GeneralClaimSettled, ReinsurancePolicies, ReinsuranceDbContext>(schema, "GeneralClaimSettled");
        services.AddIntegrationEventHandler<LifeClaimSettled, ReinsurancePolicies, ReinsuranceDbContext>(schema, "LifeClaimSettled");
        return services;
    }
}
