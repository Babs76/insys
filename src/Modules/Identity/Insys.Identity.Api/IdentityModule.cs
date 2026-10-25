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
using Insys.BuildingBlocks.Api;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.Identity.Api.StaticData;
using Insys.Identity.Application.Access;
using Insys.Identity.Application.Tenants;
using Insys.Identity.Application.Users;
using Insys.Identity.Domain.Access;
using Insys.Identity.Contracts;
using Insys.Identity.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Identity.Api;

/// <param name="ExternalSubject">For a federated identity provider: the user's subject there. Not given for INSYS's own realm (idpCode KEYCLOAK).</param>
/// <param name="Email">For INSYS's own realm: INSYS creates the Keycloak user and Keycloak sends the invitation here.</param>
public sealed record ProvisionUserRequest(string Username, string DisplayName, string IdpCode, string? ExternalSubject, bool MfaEnrolled, string? Email = null);

/// <summary>A new tenant's first administrator (OQ-067): the user, and the permissions of the tenant's TENANT_ADMIN role.</summary>
public sealed record ProvisionTenantAdministratorRequest(Guid TenantId, string Username, string DisplayName, string IdpCode, string? ExternalSubject,
    bool MfaEnrolled, string[] Permissions, string? Email = null);

public sealed record SubjectLinkRequest(Domain.Users.SubjectKind Kind, Guid? PartyId, string? IntermediaryCode, string? ProviderId);

public sealed record AssignRoleRequest(string RoleCode, DateOnly? ValidFrom, DateOnly? ValidTo, string[]? ScopeProducts, string[]? ScopeBranches);

public sealed record RevokeRoleRequest(string RoleCode);

public sealed record PreferredLanguageRequest(string? Language);

public sealed record RequestAccessRequest(Guid UserId, string Purpose, DateTimeOffset ValidFrom, DateTimeOffset ValidTo);

public sealed record RejectAccessRequest(string Reason);

public sealed record StartReviewRequest(ReviewScope Scope, Guid ReviewerId, DateOnly DueDate);

/// <summary>The Identity module: tenant-scoped users and role assignments (IAM-001), resolved per request into permissions.</summary>
public sealed class IdentityModule : IModule
{
    public string Name => "Identity";

    public IReadOnlyList<BuildingBlocks.Api.Functions.FunctionDeclaration> Functions => IdentityFunctions.All;

    /// <summary>A platform module: open to the operator tenant (ADR-024).</summary>
    public bool HoldsBusinessData => false;

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddIdentityInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        IdentityStaticDataEndpoints.MapStaticData(api);
        var me = api.MapGroup("/identity/me").WithTags(Name);
        me.MapGet("/", async (IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                (await dispatcher.Query(new GetMe(), ct)).ToHttp(view =>
                {
                    Concurrency.SetETag(http, view.RowVersion);
                    return Results.Ok(view);
                }))
            .Produces<MeView>(StatusCodes.Status200OK)
            .WithName("GetMe")
            .WithSummary("The signed-in user: profile, tenant, languages, role assignments and effective permissions with their scopes (GAP-003)")
            .RequireSignedInUser()
            .ForPortal(BuildingBlocks.Application.Context.ExternalSubject.Party, BuildingBlocks.Application.Context.ExternalSubject.Intermediary,
                BuildingBlocks.Application.Context.ExternalSubject.Provider);

        me.MapGet("/menu", async (IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Query(new GetMyMenu(), ct)).ToHttp(view => Results.Ok(view)))
            .Produces<MenuView>(StatusCodes.Status200OK)
            .WithName("GetMyMenu")
            .WithSummary("The signed-in user's menu: the domains, sub-domains, screens and actions they may use now (ADR-026 §3)")
            .WithDescription("Filtered by the platform's catalogue, the tenant's enablement and feature entitlements, the user's roles in effect and their "
                + "audience (CUSTOMER, BROKER or PROVIDER for an external user, PLATFORM in the operator tenant, else STAFF). While the tenant is Onboarding "
                + "only the functions open then appear. Ordered by the catalogue; labelKey is the translation key, label the text in the request's language.")
            .RequireSignedInUser()
            .ForPortal(BuildingBlocks.Application.Context.ExternalSubject.Party, BuildingBlocks.Application.Context.ExternalSubject.Intermediary,
                BuildingBlocks.Application.Context.ExternalSubject.Provider);

        me.MapPut("/preferred-language", (PreferredLanguageRequest request, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                Versioned(http, v => dispatcher.Send(new ChangeMyPreferredLanguage(request.Language, v), ct)))
            .Produces(StatusCodes.Status204NoContent)
            .RequireIfMatch()
            .WithName("ChangeMyPreferredLanguage")
            .WithSummary("Set or clear the signed-in user's preferred language (a language in the register)")
            .RequireSignedInUser();

        api.MapGet("/identity/roles", async (IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Query(new ListRoles(), ct)).ToHttp(roles => Results.Ok(roles)))
            .WithTags(Name)
            .Produces<IReadOnlyList<RoleView>>(StatusCodes.Status200OK)
            .WithName("ListRoles")
            .WithSummary("List the roles the tenant can assign")
            .WithDescription("Each role says whether it is privileged, requires MFA and needs a second administrator's approval to assign (GAP-021, OQ-038). "
                + "Assigning a role that needs approval creates a PendingApproval assignment that gives no permission until approved.")
            .RequirePermission(IdentityPermissions.UserRead);

        api.MapGet("/identity/role-assignments/scoped", async (IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Query(new ListScopedAssignments(), ct)).ToHttp(items => Results.Ok(items)))
            .WithTags(Name)
            .Produces<IReadOnlyList<ScopedAssignmentView>>(StatusCodes.Status200OK)
            .WithName("IdentityListScopedAssignments")
            .WithSummary("Role assignments with product or organisation scopes, for review (F103)")
            .WithDescription("Each gives its role's permissions only for the records of its products and organisation nodes (with the nodes below them).")
            .RequirePermission(IdentityPermissions.UserRead);

        var users = api.MapGroup("/identity/users").WithTags(Name);

        users.MapPost("/", async (ProvisionUserRequest request, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                (await dispatcher.Send(new ProvisionUser(request.Username, request.DisplayName, request.IdpCode, request.ExternalSubject, request.MfaEnrolled, request.Email), ct))
                .ToHttp(id =>
                {
                    Concurrency.SetETag(http, 1);
                    return Results.Created($"/api/v1/identity/users/{id}", new { id });
                }))
            .Produces<CreatedId>(StatusCodes.Status201Created)
            .WithName("ProvisionUser")
            .WithSummary("Provision a user")
            .WithDescription("For INSYS's own realm (idpCode KEYCLOAK) give email and no externalSubject: the user is PendingIdentity until INSYS has created its "
                + "Keycloak user, then Invited (Keycloak emails it to set a password, and a one-time code with mfaEnrolled) until its first sign-in. For a "
                + "federated identity provider give externalSubject: the user is Invited until the first request with a token for it. A user with no roles "
                + "can sign in, and do nothing until a role is assigned.")
            .WithEventualEffects("for INSYS's own realm, the user becomes Invited once its Keycloak user is created; if Keycloak cannot be reached, the "
                + "creation is parked (KEYCLOAK_UNAVAILABLE) and retried.")
            .RequirePermission(IdentityPermissions.UserCreate)
            .RequireIdempotencyKey();

        users.MapPost("/{id:guid}/resend-invitation", (Guid id, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                Versioned(http, version => dispatcher.Send(new ResendInvitation(id, version), ct)))
            .Produces(StatusCodes.Status204NoContent)
            .RequireIfMatch()
            .WithName("ResendInvitation")
            .WithSummary("Send Keycloak's invitation email again, to a user of INSYS's own realm who has not signed in yet")
            .WithEventualEffects("Keycloak sends the email; if it cannot be reached, sending is parked (KEYCLOAK_UNAVAILABLE) and retried.")
            .RequirePermission(IdentityPermissions.UserUpdate);

        users.MapGet("/", async (string? cursor, int? limit, IDispatcher dispatcher, CancellationToken ct) =>
            {
                if (!Cursor.TryDecode(cursor, out var afterId))
                {
                    return Problems.For(BuildingBlocks.Domain.ErrorCodes.ValidationFailed, "The cursor is not valid.", StatusCodes.Status422UnprocessableEntity);
                }

                var pageSize = Cursor.ClampLimit(limit);
                return (await dispatcher.Query(new ListUsers(afterId, pageSize), ct))
                    .ToHttp(items => Results.Ok(new CursorPage<UserView>(items, items.Count == pageSize ? Cursor.Encode(items[^1].Id) : null)));
            })
            .Produces<CursorPage<UserView>>(StatusCodes.Status200OK)
            .WithName("ListUsers")
            .WithSummary("List users")
            .RequirePermission(IdentityPermissions.UserRead);

        users.MapGet("/{id:guid}", async (Guid id, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                (await dispatcher.Query(new GetUser(id), ct)).ToHttp(user =>
                {
                    Concurrency.SetETag(http, user.RowVersion);
                    return Results.Ok(user);
                }))
            .Produces<UserView>(StatusCodes.Status200OK)
            .WithName("GetUser")
            .WithSummary("Get a user with its role assignments")
            .RequirePermission(IdentityPermissions.UserRead);

        users.MapPost("/{id:guid}/subject-link", (Guid id, SubjectLinkRequest request, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                Versioned(http, version => dispatcher.Send(new LinkSubject(id, request.Kind, request.PartyId, request.IntermediaryCode, request.ProviderId, version), ct)))
            .Produces(StatusCodes.Status204NoContent)
            .RequireIfMatch()
            .WithName("IdentityLinkSubject")
            .WithSummary("Link an external user to the party, intermediary or provider they are")
            .WithDescription("External-user access §1: one link per user, to a party (member portal), an intermediary (broker portal, active and licensed) or a "
                + "provider (provider portal). It gives nothing until a second person approves it.")
            .WithEventualEffects("the EXTERNAL_SUBJECT_LINK approval appears in Workflow; the link stays Pending until it is decided.")
            .RequirePermission(IdentityPermissions.UserUpdate);

        users.MapPost("/{id:guid}/subject-link/end", (Guid id, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                Versioned(http, version => dispatcher.Send(new EndSubjectLink(id, version), ct)))
            .Produces(StatusCodes.Status204NoContent)
            .RequireIfMatch()
            .WithName("IdentityEndSubjectLink")
            .WithSummary("End an external user's link: their next request is already without it")
            .RequirePermission(IdentityPermissions.UserUpdate);

        users.MapGet("/{id:guid}/subject-link", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Query(new GetSubjectLink(id), ct)).ToHttp(Results.Ok))
            .Produces<SubjectLinkView>(StatusCodes.Status200OK)
            .WithName("IdentityGetSubjectLink")
            .WithSummary("An external user's link pending or in effect")
            .RequirePermission(IdentityPermissions.UserRead);

        users.MapPost("/{id:guid}/assign-role", (Guid id, AssignRoleRequest request, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                Versioned(http, version => dispatcher.Send(new AssignRole(id, request.RoleCode, request.ValidFrom, request.ValidTo, request.ScopeProducts,
                    request.ScopeBranches, version), ct)))
            .Produces(StatusCodes.Status204NoContent)
            .RequireIfMatch()
            .WithName("AssignRole")
            .WithSummary("Assign a role")
            .WithDescription("Refused for roles with no version in force, MFA-required roles without MFA (IAM-003) and segregation-of-duties conflicts "
                + "(SRS-002). Product and organisation scopes limit the role's permissions to those records (F103).")
            .WithEventualEffects("for a privileged role (requiresApproval), the PRIVILEGED_ROLE approval appears in Workflow; the assignment stays "
                + "PendingApproval until it is decided.")
            .RequirePermission(IdentityPermissions.UserUpdate);

        users.MapPost("/{id:guid}/revoke-role", (Guid id, RevokeRoleRequest request, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                Versioned(http, version => dispatcher.Send(new RevokeRole(id, request.RoleCode, version), ct)))
            .Produces(StatusCodes.Status204NoContent)
            .RequireIfMatch()
            .WithName("RevokeRole")
            .WithSummary("Revoke a role")
            .WithDescription("Takes effect on the user's next request (IAM-005).")
            .RequirePermission(IdentityPermissions.UserUpdate);

        MapAccess(api.MapGroup("/identity").WithTags(Name));
        MapTenantAdministrators(api.MapGroup("/identity/tenant-administrators").WithTags(Name));

        MapStatus(users, "lock", UserStatusAction.Lock, "Lock a user");
        MapStatus(users, "unlock", UserStatusAction.Unlock, "Unlock a user");
        MapStatus(users, "disable", UserStatusAction.Disable, "Disable a user and revoke their sessions (IAM-005)");
    }

    private static void MapAccess(RouteGroupBuilder identity)
    {
        var grants = identity.MapGroup("/privileged-access");
        grants.MapPost("/", async (RequestAccessRequest request, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                (await dispatcher.Send(new RequestAccess(request.UserId, request.Purpose, request.ValidFrom, request.ValidTo), ct)).ToHttp(id =>
                {
                    Concurrency.SetETag(http, 1);
                    return Results.Created($"/api/v1/identity/privileged-access/{id}", new { id });
                }))
            .Produces<CreatedId>(StatusCodes.Status201Created)
            .WithName("RequestAccess")
            .WithSummary("Request time-limited privileged access")
            .WithDescription("A purpose is recorded and the window is limited; someone other than the requester approves it (TEN-007).")
            .RequirePermission(IdentityPermissions.PrivilegedAccessCreate)
            .RequireIdempotencyKey();

        grants.MapGet("/", async (string? status, string? cursor, int? limit, IDispatcher dispatcher, CancellationToken ct) =>
            {
                if (!Cursor.TryDecode(cursor, out var afterId))
                {
                    return Problems.For(BuildingBlocks.Domain.ErrorCodes.ValidationFailed, "The cursor is not valid.", StatusCodes.Status422UnprocessableEntity);
                }

                var pageSize = Cursor.ClampLimit(limit);
                return (await dispatcher.Query(new ListGrants(status, afterId, pageSize), ct))
                    .ToHttp(items => Results.Ok(new CursorPage<GrantView>(items, items.Count == pageSize ? Cursor.Encode(items[^1].Id) : null)));
            })
            .Produces<CursorPage<GrantView>>(StatusCodes.Status200OK)
            .WithName("ListPrivilegedAccess")
            .WithSummary("List privileged access requests and grants")
            .RequirePermission(IdentityPermissions.PrivilegedAccessRead);

        grants.MapGet("/{id:guid}", async (Guid id, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                (await dispatcher.Query(new GetGrant(id), ct)).ToHttp(grant =>
                {
                    Concurrency.SetETag(http, grant.RowVersion);
                    return Results.Ok(grant);
                }))
            .Produces<GrantView>(StatusCodes.Status200OK)
            .WithName("GetPrivilegedAccess")
            .WithSummary("Get a privileged access request or grant")
            .RequirePermission(IdentityPermissions.PrivilegedAccessRead);

        grants.MapPost("/{id:guid}/approve", (Guid id, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                Versioned(http, version => dispatcher.Send(new ApproveAccess(id, version), ct)))
            .Produces(StatusCodes.Status204NoContent)
            .RequireIfMatch()
            .WithName("ApproveAccess")
            .WithSummary("Approve privileged access (not by the requester)")
            .RequirePermission(IdentityPermissions.PrivilegedAccessApprove);

        grants.MapPost("/{id:guid}/reject", (Guid id, RejectAccessRequest request, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                Versioned(http, version => dispatcher.Send(new RejectAccess(id, request.Reason, version), ct)))
            .Produces(StatusCodes.Status204NoContent)
            .RequireIfMatch()
            .WithName("RejectAccess")
            .WithSummary("Reject privileged access, with a reason")
            .RequirePermission(IdentityPermissions.PrivilegedAccessApprove);

        var reviews = identity.MapGroup("/access-reviews");
        reviews.MapPost("/", async (StartReviewRequest request, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                (await dispatcher.Send(new StartReview(request.Scope, request.ReviewerId, request.DueDate), ct)).ToHttp(id =>
                {
                    Concurrency.SetETag(http, 1);
                    return Results.Created($"/api/v1/identity/access-reviews/{id}", new { id });
                }))
            .Produces<CreatedId>(StatusCodes.Status201Created)
            .WithName("StartReview")
            .WithSummary("Start an access review")
            .WithDescription("Takes a snapshot of the role assignments in scope (AllUsers or Privileged) as review items.")
            .RequirePermission(IdentityPermissions.AccessReviewCreate)
            .RequireIdempotencyKey();

        reviews.MapGet("/", async (string? cursor, int? limit, IDispatcher dispatcher, CancellationToken ct) =>
            {
                if (!Cursor.TryDecode(cursor, out var afterId))
                {
                    return Problems.For(BuildingBlocks.Domain.ErrorCodes.ValidationFailed, "The cursor is not valid.", StatusCodes.Status422UnprocessableEntity);
                }

                var pageSize = Cursor.ClampLimit(limit);
                return (await dispatcher.Query(new ListReviews(afterId, pageSize), ct))
                    .ToHttp(items => Results.Ok(new CursorPage<ReviewView>(items, items.Count == pageSize ? Cursor.Encode(items[^1].Id) : null)));
            })
            .Produces<CursorPage<ReviewView>>(StatusCodes.Status200OK)
            .WithName("ListAccessReviews")
            .WithSummary("List access reviews")
            .RequirePermission(IdentityPermissions.AccessReviewRead);

        reviews.MapGet("/{id:guid}", async (Guid id, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                (await dispatcher.Query(new GetReview(id), ct)).ToHttp(review =>
                {
                    Concurrency.SetETag(http, review.RowVersion);
                    return Results.Ok(review);
                }))
            .Produces<ReviewView>(StatusCodes.Status200OK)
            .WithName("GetAccessReview")
            .WithSummary("Get an access review with its items")
            .RequirePermission(IdentityPermissions.AccessReviewRead);

        reviews.MapPost("/{id:guid}/items/{itemId:guid}/confirm", (Guid id, Guid itemId, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                Versioned(http, version => dispatcher.Send(new DecideReviewItem(id, itemId, Confirm: true, version), ct)))
            .Produces(StatusCodes.Status204NoContent)
            .RequireIfMatch()
            .WithName("ConfirmAccess")
            .WithSummary("Confirm a reviewed access")
            .RequirePermission(IdentityPermissions.AccessReviewUpdate);

        reviews.MapPost("/{id:guid}/items/{itemId:guid}/revoke", (Guid id, Guid itemId, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                Versioned(http, version => dispatcher.Send(new DecideReviewItem(id, itemId, Confirm: false, version), ct)))
            .Produces(StatusCodes.Status204NoContent)
            .RequireIfMatch()
            .WithName("RevokeAccess")
            .WithSummary("Revoke a reviewed access")
            .WithDescription("The role ends when the review completes (IAM-005).")
            .RequirePermission(IdentityPermissions.AccessReviewUpdate);

        reviews.MapPost("/{id:guid}/complete", (Guid id, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                Versioned(http, version => dispatcher.Send(new CompleteReview(id, version), ct)))
            .Produces(StatusCodes.Status204NoContent)
            .RequireIfMatch()
            .WithName("CompleteReview")
            .WithSummary("Complete an access review")
            .WithDescription("Every item must be confirmed or revoked first. Revoked roles end at once (IAM-005).")
            .RequirePermission(IdentityPermissions.AccessReviewUpdate);
    }

    private static void MapStatus(RouteGroupBuilder users, string action, UserStatusAction statusAction, string summary) =>
        users.MapPost($"/{{id:guid}}/{action}", (Guid id, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                Versioned(http, version => dispatcher.Send(new ChangeUserStatus(id, statusAction, version), ct)))
            .Produces(StatusCodes.Status204NoContent)
            .RequireIfMatch()
            .WithName($"{statusAction}User")
            .WithSummary(summary)
            .RequirePermission(IdentityPermissions.UserUpdate);

    private static async Task<IResult> Versioned(HttpContext http, Func<long, Task<BuildingBlocks.Domain.Result<long>>> send)
    {
        var expected = Concurrency.ExpectedVersion(http.Request);
        if (expected.IsFailure)
        {
            return Problems.For(expected.Error);
        }

        return (await send(expected.Value)).ToHttp(version =>
        {
            Concurrency.SetETag(http, version);
            return Results.NoContent();
        });
    }

    /// <summary>A new tenant's first administrator (OQ-067, GAP-019): requested and approved by two platform administrators.</summary>
    private static void MapTenantAdministrators(RouteGroupBuilder requests)
    {
        requests.MapPost("/", async (ProvisionTenantAdministratorRequest request, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                (await dispatcher.Send(new ProvisionTenantAdministrator(request.TenantId, request.Username, request.DisplayName, request.IdpCode,
                    request.ExternalSubject, request.MfaEnrolled, request.Permissions ?? [], request.Email), ct))
                .ToHttp(id =>
                {
                    Concurrency.SetETag(http, 1);
                    return Results.Created($"/api/v1/identity/tenant-administrators/{id}", new { id });
                }))
            .Produces<CreatedId>(StatusCodes.Status201Created)
            .WithName("ProvisionTenantAdministrator")
            .WithSummary("Request a new tenant's first administrator")
            .WithDescription("Platform operator only, while the tenant is Onboarding and has no users. A second platform administrator approves it in "
                + "Workflow (approval type TENANT_ADMINISTRATOR); then the tenant's TENANT_ADMIN role (with these permissions), the user and an active "
                + "assignment are created, and the ORGANISATION_AND_ACCESS onboarding step completes when it is the current one. The user signs in with "
                + "externalSubject, whose token must carry the new tenant's tenant_id.")
            .WithEventualEffects("the TENANT_ADMINISTRATOR approval appears in Workflow; after approval the request becomes Provisioned and Tenancy "
                + "completes the onboarding step.")
            .RequirePlatformPermission(IdentityPermissions.UserCreate)
            .RequireIdempotencyKey();

        requests.MapGet("/", async (Guid? tenantId, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Query(new ListTenantAdministratorRequests(tenantId), ct)).ToHttp(items => Results.Ok(items)))
            .Produces<IReadOnlyList<TenantAdministratorRequestView>>(StatusCodes.Status200OK)
            .WithName("ListTenantAdministratorRequests")
            .WithSummary("List first-administrator requests, optionally for one tenant")
            .RequirePlatformPermission(IdentityPermissions.UserRead);

        requests.MapGet("/{id:guid}", async (Guid id, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                (await dispatcher.Query(new GetTenantAdministratorRequest(id), ct)).ToHttp(request =>
                {
                    Concurrency.SetETag(http, request.RowVersion);
                    return Results.Ok(request);
                }))
            .Produces<TenantAdministratorRequestView>(StatusCodes.Status200OK)
            .WithName("GetTenantAdministratorRequest")
            .WithSummary("Get a first-administrator request and its decision")
            .RequirePlatformPermission(IdentityPermissions.UserRead);
    }
}
