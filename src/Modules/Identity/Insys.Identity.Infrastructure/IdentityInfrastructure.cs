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
        // Its approval kinds and the screens that open their records (BR-005).
        BuildingBlocks.Contracts.Approvals.ApprovalKinds.Declare(Insys.Identity.Contracts.IdentityApprovals.BusinessRefType, "IDENTITY_ROLE_ASSIGNMENTS", Insys.Identity.Contracts.IdentityApprovals.ApprovalType);
        BuildingBlocks.Contracts.Approvals.ApprovalKinds.Declare(Insys.Identity.Contracts.IdentityApprovals.TenantAdministratorBusinessRefType, "IDENTITY_TENANT_ADMINISTRATORS", Insys.Identity.Contracts.IdentityApprovals.TenantAdministratorApprovalType);
        BuildingBlocks.Contracts.Approvals.ApprovalKinds.RaisedWith(BuildingBlocks.Contracts.Approvals.ApprovalKinds.PlatformOperator,
            Insys.Identity.Contracts.IdentityApprovals.TenantAdministratorApprovalType);
        BuildingBlocks.Contracts.Approvals.ApprovalKinds.Declare(Insys.Identity.Contracts.IdentityApprovals.SubjectLinkBusinessRefType, "IDENTITY_USERS", Insys.Identity.Contracts.IdentityApprovals.SubjectLinkApprovalType);
        // F103 (OQ-176): a user's access is no product's or branch's record.
        BuildingBlocks.Contracts.Approvals.ApprovalKinds.Unscoped(Insys.Identity.Contracts.IdentityApprovals.BusinessRefType, "a user's role assignment: identity, not a scoped record");
        BuildingBlocks.Contracts.Approvals.ApprovalKinds.Unscoped(Insys.Identity.Contracts.IdentityApprovals.TenantAdministratorBusinessRefType, "a tenant's first administrators, in the operator tenant");
        BuildingBlocks.Contracts.Approvals.ApprovalKinds.Unscoped(Insys.Identity.Contracts.IdentityApprovals.SubjectLinkBusinessRefType, "an external user's link to their party");
        services.AddModuleDbContext<IdentityDbContext>(IdentityDbContext.SchemaName, "Identity");
        services.AddStaticData<IdentityDbContext>(IdentityDbContext.SchemaName, IdentityStaticData.Descriptors);
        services.AddSingleton<IStaticRecordValidator, StaticData.RoleDefinitionValidator>();
        services.AddSingleton<IStaticRecordValidator, StaticData.DutyConflictRuleValidator>();
        // OQ-190 (Ade, 19 November 2026): a role holding ASSIST_PRODUCT_DRAFT_RUN must hold ASSIST_PRODUCT_DRAFT_REVIEW too.
        services.AddSingleton<StaticData.FunctionRequirementGuard>();
        services.AddSingleton<IStaticSetGuard>(sp => sp.GetRequiredService<StaticData.FunctionRequirementGuard>());
        services.AddSingleton<IStaticRemovalGuard>(sp => sp.GetRequiredService<StaticData.FunctionRequirementGuard>());
        services.AddSingleton<IStaticSubmissionGuard, StaticData.RoleSegregationGuard>();
        // OQ-197 (Ade, 20 November 2026): a tenant can't retire or weaken the platform's segregation-of-duties baseline.
        services.AddSingleton<StaticData.SegregationBaselineGuard>();
        services.AddSingleton<IStaticChangeGuard>(sp => sp.GetRequiredService<StaticData.SegregationBaselineGuard>());
        services.AddSingleton<IStaticRemovalGuard>(sp => sp.GetRequiredService<StaticData.SegregationBaselineGuard>());
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
        services.AddSingleton<FunctionCatalogueReader>();
        services.AddSingleton<Application.Users.IFunctionCatalogueQuery, FunctionCatalogueQuery>();
        services.AddSingleton<Application.Users.IMenuBuilder, MenuBuilder>();
        services.AddSingleton<FunctionAvailability>();
        services.AddSingleton<BuildingBlocks.Application.Context.IFunctionAvailability>(sp => sp.GetRequiredService<FunctionAvailability>());
        services.AddHealthChecks().AddCheck<JobFunctionsHealthCheck>("job-functions");
        services.AddSingleton<IPrincipalResolver, PrincipalResolver>();
        services.AddSingleton<Contracts.IUserRoleQuery, UserRoleQuery>();
        services.AddSingleton<BuildingBlocks.Application.Context.IServiceAccounts, UserRoleQuery>();
        services.AddSingleton<Application.Users.IDisplayNames, DisplayNames>(); // BR-021
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
        services.AddIntegrationEventHandler<BuildingBlocks.Contracts.StaticData.StaticDataVersionActivated, FunctionCacheInvalidation, IdentityDbContext>(
            IdentityDbContext.SchemaName, "FunctionsActivated");

        services.AddIntegrationEventHandler<Workflow.Contracts.ApprovalGranted, PrivilegedRoleApprovalPolicy, IdentityDbContext>(IdentityDbContext.SchemaName, "ApprovalGranted");
        services.AddIntegrationEventHandler<Workflow.Contracts.ApprovalRejected, PrivilegedRoleApprovalPolicy, IdentityDbContext>(IdentityDbContext.SchemaName, "ApprovalRejected");

        services.AddScoped<Application.Tenants.ITenantAdministrators, TenantAdministratorStore>();
        services.AddIntegrationEventHandler<Workflow.Contracts.ApprovalGranted, Application.Tenants.TenantAdministratorApprovalPolicy, IdentityDbContext>(
            IdentityDbContext.SchemaName, "ApprovalGranted");
        services.AddIntegrationEventHandler<Workflow.Contracts.ApprovalRejected, Application.Tenants.TenantAdministratorApprovalPolicy, IdentityDbContext>(
            IdentityDbContext.SchemaName, "ApprovalRejected");
        // OQ-099 follow-up (Ade, 21 November 2026): a grant the policy above parked as not applied ends the request NotApplied.
        services.AddIntegrationEventHandler<Integration.Contracts.DeliveryParked, Application.Tenants.TenantAdministratorNotAppliedPolicy, IdentityDbContext>(
            IdentityDbContext.SchemaName);

        services.AddScoped<IAccessRepository, AccessRepository>();
        services.AddScoped<SegregationConflictFinder>();
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
            // BR-013 (decided 26 October 2026): two active administrators, so that one can approve what the other does. Active means
            // an active TENANT_ADMIN assignment, the identity linked, and at least one successful authenticated request (last_login_at).
            var admins = await BuildingBlocks.Infrastructure.Readiness.ReadinessSql.Count(connection, transaction, """
                SELECT count(DISTINCT a.user_id) FROM iam.user_role_assignment a
                JOIN iam.user_account u ON u.tenant_id = a.tenant_id AND u.id = a.user_id
                WHERE a.tenant_id = @t AND a.role_code = 'TENANT_ADMIN' AND a.status = 'ACTIVE' AND a.valid_from <= @day AND (a.valid_to IS NULL OR a.valid_to > @day)
                  AND u.status = 'ACTIVE' AND u.external_subject IS NOT NULL AND u.last_login_at IS NOT NULL
                """, ct, ("t", context.TenantId), ("day", context.Day));
            return [BuildingBlocks.Application.ReadinessResult.Of("ORGANISATION_AND_ACCESS", "FIRST_ADMINISTRATOR",
                "Two active tenant administrators: identity linked and signed in at least once (OQ-067, BR-013)",
                admins >= 2 ? [] : [$"TENANT_ADMIN: {admins} of 2 active"])];
        });
        services.AddSingleton<Persistence.StaffRoles>();
        services.AddSingleton<Contracts.IStaffRoles>(sp => sp.GetRequiredService<Persistence.StaffRoles>());
        services.AddSingleton<Contracts.IPortalRoles>(sp => sp.GetRequiredService<Persistence.StaffRoles>());
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
