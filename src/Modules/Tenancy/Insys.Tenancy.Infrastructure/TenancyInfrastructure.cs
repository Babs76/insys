using Insys.BuildingBlocks.Infrastructure;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Tenancy.Infrastructure.Persistence;
using Insys.Tenancy.Infrastructure.StaticData;
using Insys.BuildingBlocks.Application.Context;
using Insys.Tenancy.Application.Tenants;
using Insys.Tenancy.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Tenancy.Infrastructure;

public static class TenancyInfrastructure
{
    public static IServiceCollection AddTenancyInfrastructure(this IServiceCollection services)
    {
        services.AddModuleDbContext<TenancyDbContext>(TenancyDbContext.SchemaName, "Tenancy");
        services.AddStaticData<TenancyDbContext>(TenancyDbContext.SchemaName, TenancyStaticData.Descriptors);
        services.AddModuleHandlers<TenancyDbContext>(typeof(RegisterTenant).Assembly);
        services.AddDomainEventMapper<TenancyEventMapper>(TenancyDbContext.SchemaName);
        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<ITenantQueries, TenantQueries>();
        services.AddSingleton<ITenantDirectory, TenantDirectory>();
        services.AddSingleton<ITenantStatusQuery, TenantStatusQuery>();
        return services;
    }
}
