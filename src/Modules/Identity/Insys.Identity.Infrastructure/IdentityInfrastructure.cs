using Insys.BuildingBlocks.Infrastructure;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Identity.Infrastructure.Persistence;
using Insys.Identity.Infrastructure.StaticData;
using Insys.BuildingBlocks.Application.Context;
using Insys.Identity.Application.Access;
using Insys.Identity.Application.Users;
using Insys.Tenancy.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Identity.Infrastructure;

public static class IdentityInfrastructure
{
    public static IServiceCollection AddIdentityInfrastructure(this IServiceCollection services)
    {
        services.AddModuleDbContext<IdentityDbContext>(IdentityDbContext.SchemaName, "Identity");
        services.AddStaticData<IdentityDbContext>(IdentityDbContext.SchemaName, IdentityStaticData.Descriptors);
        services.AddModuleHandlers<IdentityDbContext>(typeof(ProvisionUser).Assembly);
        services.AddDomainEventMapper<IdentityEventMapper>(IdentityDbContext.SchemaName);
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IUserQueries, UserQueries>();
        services.AddScoped<IRolePolicyReader, RolePolicyReader>();

        services.AddMemoryCache();
        services.AddOptions<PermissionCacheOptions>().BindConfiguration("Identity:PermissionCache");
        services.AddSingleton<PermissionCache>();
        services.AddSingleton<IPermissionCache>(sp => sp.GetRequiredService<PermissionCache>());
        services.AddSingleton<IPrincipalResolver, PrincipalResolver>();
        if (!InfrastructureRegistration.IsBuildTimeDocumentGeneration)
        {
            // Registered first among Identity's hosted services, so a refused instance stops before any background work starts.
            services.AddHostedService<SingleInstanceGuard>();
        }
        services.AddIntegrationEventHandler<Contracts.RoleAssigned, PermissionCacheInvalidation, IdentityDbContext>(IdentityDbContext.SchemaName, "RoleAssigned");
        services.AddIntegrationEventHandler<Contracts.RoleRevoked, PermissionCacheInvalidation, IdentityDbContext>(IdentityDbContext.SchemaName, "RoleRevoked");
        services.AddIntegrationEventHandler<Contracts.UserDisabled, PermissionCacheInvalidation, IdentityDbContext>(IdentityDbContext.SchemaName, "UserDisabled");
        services.AddIntegrationEventHandler<TenantSuspended, PermissionCacheInvalidation, IdentityDbContext>(IdentityDbContext.SchemaName, "TenantSuspended");

        services.AddScoped<IAccessRepository, AccessRepository>();
        services.AddScoped<IAccessQueries, AccessQueries>();
        services.AddIntegrationEventHandler<Contracts.AccessReviewCompleted, RevokeReviewedAccess, IdentityDbContext>(IdentityDbContext.SchemaName);
        services.AddOptions<PrivilegedAccessOptions>().BindConfiguration("Identity:PrivilegedAccess");
        services.AddSingleton<PrivilegedAccessExpiry>();
        if (!InfrastructureRegistration.IsBuildTimeDocumentGeneration)
        {
            services.AddHostedService<PrivilegedAccessExpiryService>();
        }
        return services;
    }
}
