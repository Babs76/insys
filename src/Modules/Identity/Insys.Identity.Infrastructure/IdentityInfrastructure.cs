/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Identity
 * CONFIDENTIALITY AND INTELLECTUAL PROPERTY NOTICE
 * This source code and its associated documentation contain confidential and
 * proprietary information belonging to OpenFactor Technology Group or its
 * applicable intellectual property rights holder.
 * This material is provided solely for authorised development, maintenance,
 * operation or evaluation purposes, as applicable. No person may reproduce,
 * modify, distribute, disclose, publish or otherwise use this material except
 * as expressly permitted by an applicable written agreement or licence.
 * No ownership rights or licence to any intellectual property are granted
 * by the inclusion of this notice, except as expressly provided in an
 * applicable written agreement or licence.
 * Third-party software, open-source components and customer-owned materials
 * remain subject to their respective ownership rights and applicable licences
 * or agreements.
 * UNAUTHORISED ACCESS, USE, COPYING OR DISCLOSURE IS PROHIBITED.
 * ============================================================================
 */
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
        services.AddSingleton<Contracts.IUserRoleQuery, UserRoleQuery>();
        if (!InfrastructureRegistration.IsBuildTimeDocumentGeneration)
        {
            // Registered first among Identity's hosted services, so a refused instance stops before any background work starts.
            services.AddHostedService<SingleInstanceGuard>();
        }
        services.AddIntegrationEventHandler<Contracts.RoleAssigned, PermissionCacheInvalidation, IdentityDbContext>(IdentityDbContext.SchemaName, "RoleAssigned");
        services.AddIntegrationEventHandler<Contracts.RoleRevoked, PermissionCacheInvalidation, IdentityDbContext>(IdentityDbContext.SchemaName, "RoleRevoked");
        services.AddIntegrationEventHandler<Contracts.UserDisabled, PermissionCacheInvalidation, IdentityDbContext>(IdentityDbContext.SchemaName, "UserDisabled");
        services.AddIntegrationEventHandler<TenantSuspended, PermissionCacheInvalidation, IdentityDbContext>(IdentityDbContext.SchemaName, "TenantSuspended");

        services.AddIntegrationEventHandler<Workflow.Contracts.ApprovalGranted, PrivilegedRoleApprovalPolicy, IdentityDbContext>(IdentityDbContext.SchemaName, "ApprovalGranted");
        services.AddIntegrationEventHandler<Workflow.Contracts.ApprovalRejected, PrivilegedRoleApprovalPolicy, IdentityDbContext>(IdentityDbContext.SchemaName, "ApprovalRejected");

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
