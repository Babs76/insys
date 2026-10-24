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
        services.AddSingleton<BuildingBlocks.Application.Context.IPortalAuthentication, PortalAuthentication>();
        services.AddSingleton<IPrincipalResolver, PrincipalResolver>();
        services.AddSingleton<Contracts.IUserRoleQuery, UserRoleQuery>();
        if (!InfrastructureRegistration.IsBuildTimeDocumentGeneration)
        {
            // Registered first among Identity's hosted services, so a refused instance stops before any background work starts.
            services.AddHostedService<SingleInstanceGuard>();
        }
        services.AddIntegrationEventHandler<Contracts.RoleAssigned, PermissionCacheInvalidation, IdentityDbContext>(IdentityDbContext.SchemaName, "RoleAssigned");
        services.AddIntegrationEventHandler<Contracts.SubjectLinkChanged, PermissionCacheInvalidation, IdentityDbContext>(IdentityDbContext.SchemaName, "SubjectLinkChanged");
        services.AddIntegrationEventHandler<Contracts.RoleRevoked, PermissionCacheInvalidation, IdentityDbContext>(IdentityDbContext.SchemaName, "RoleRevoked");
        services.AddIntegrationEventHandler<Contracts.UserDisabled, PermissionCacheInvalidation, IdentityDbContext>(IdentityDbContext.SchemaName, "UserDisabled");
        services.AddIntegrationEventHandler<TenantSuspended, PermissionCacheInvalidation, IdentityDbContext>(IdentityDbContext.SchemaName, "TenantSuspended");
        services.AddIntegrationEventHandler<TenantActivated, PermissionCacheInvalidation, IdentityDbContext>(IdentityDbContext.SchemaName, "TenantActivated");

        services.AddIntegrationEventHandler<Workflow.Contracts.ApprovalGranted, PrivilegedRoleApprovalPolicy, IdentityDbContext>(IdentityDbContext.SchemaName, "ApprovalGranted");
        services.AddIntegrationEventHandler<Workflow.Contracts.ApprovalRejected, PrivilegedRoleApprovalPolicy, IdentityDbContext>(IdentityDbContext.SchemaName, "ApprovalRejected");

        services.AddScoped<Application.Tenants.ITenantAdministrators, TenantAdministratorStore>();
        services.AddIntegrationEventHandler<Workflow.Contracts.ApprovalGranted, Application.Tenants.TenantAdministratorApprovalPolicy, IdentityDbContext>(
            IdentityDbContext.SchemaName, "ApprovalGranted");
        services.AddIntegrationEventHandler<Workflow.Contracts.ApprovalRejected, Application.Tenants.TenantAdministratorApprovalPolicy, IdentityDbContext>(
            IdentityDbContext.SchemaName, "ApprovalRejected");

        services.AddScoped<IAccessRepository, AccessRepository>();
        services.AddScoped<IAccessQueries, AccessQueries>();
        services.AddIntegrationEventHandler<Contracts.AccessReviewCompleted, RevokeReviewedAccess, IdentityDbContext>(IdentityDbContext.SchemaName);
        services.AddOptions<PrivilegedAccessOptions>().BindConfiguration("Identity:PrivilegedAccess");
        services.AddSingleton<PrivilegedAccessExpiry>();
        if (!InfrastructureRegistration.IsBuildTimeDocumentGeneration)
        {
            services.AddHostedService<PrivilegedAccessExpiryService>();
        }
        // Onboarding readiness (addenda/onboarding-readiness.md §2): the configuration this module owns.
        BuildingBlocks.Infrastructure.Readiness.ReadinessRegistration.AddStaticReadiness(services, Persistence.IdentityDbContext.SchemaName, IdentityStaticData.Descriptors,
            new BuildingBlocks.Infrastructure.Readiness.StaticCheck("ORGANISATION_AND_ACCESS", "ROLES", "iam.cfg_roles_and_permission_sets", "At least one role besides TENANT_ADMIN", "role_code <> 'TENANT_ADMIN'"),
            new BuildingBlocks.Infrastructure.Readiness.StaticCheck("ORGANISATION_AND_ACCESS", "AUTHENTICATION_POLICY", "iam.cfg_authentication_and_session_policies", "An authentication and session policy, the tenant's or global", null, GlobalRows: true));
        BuildingBlocks.Infrastructure.Readiness.ReadinessRegistration.AddSqlReadiness(services, Persistence.IdentityDbContext.SchemaName, ["ORGANISATION_AND_ACCESS"], async (context, connection, transaction, ct) =>
        {
            var admins = await BuildingBlocks.Infrastructure.Readiness.ReadinessSql.Count(connection, transaction, """
                SELECT count(*) FROM iam.user_role_assignment WHERE tenant_id = @t AND role_code = 'TENANT_ADMIN' AND status = 'ACTIVE'
                  AND valid_from <= @day AND (valid_to IS NULL OR valid_to > @day)
                """, ct, ("t", context.TenantId), ("day", context.Day));
            return [BuildingBlocks.Application.ReadinessResult.Of("ORGANISATION_AND_ACCESS", "FIRST_ADMINISTRATOR", "An active tenant administrator (OQ-067)",
                admins > 0 ? [] : ["TENANT_ADMIN"])];
        });
        services.AddSingleton<Contracts.IStaffRoles, Persistence.StaffRoles>();
        // INSYS creates the Keycloak users of its own realm (keycloak-user-provisioning).
        services.AddOptions<Keycloak.KeycloakOptions>().BindConfiguration("Identity:Keycloak");
        services.AddHttpClient("keycloak-admin");
        services.AddSingleton<Keycloak.IKeycloakAdmin, Keycloak.KeycloakAdmin>();
        services.AddIntegrationEventHandler<Contracts.UserIdentityRequested, Keycloak.KeycloakProvisioning, IdentityDbContext>(IdentityDbContext.SchemaName, "UserIdentityRequested");
        services.AddIntegrationEventHandler<Contracts.UserInvitationRequested, Keycloak.KeycloakProvisioning, IdentityDbContext>(IdentityDbContext.SchemaName,
            "UserInvitationRequested");
        services.AddIntegrationEventHandler<Contracts.UserDisabled, Keycloak.KeycloakProvisioning, IdentityDbContext>(IdentityDbContext.SchemaName, "UserDisabled");
        services.AddIntegrationEventHandler<Contracts.UserReenabled, Keycloak.KeycloakProvisioning, IdentityDbContext>(IdentityDbContext.SchemaName, "UserReenabled");
        return services;
    }
}
