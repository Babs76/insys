using Insys.BuildingBlocks.Api;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.Identity.Api.StaticData;
using Insys.Identity.Application.Access;
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

public sealed record ProvisionUserRequest(string Username, string DisplayName, string IdpCode, string ExternalSubject, bool MfaEnrolled);

public sealed record AssignRoleRequest(string RoleCode, DateOnly? ValidFrom, DateOnly? ValidTo, string[]? ScopeProducts, string[]? ScopeBranches);

public sealed record RevokeRoleRequest(string RoleCode);

public sealed record RequestAccessRequest(Guid UserId, string Purpose, DateTimeOffset ValidFrom, DateTimeOffset ValidTo);

public sealed record RejectAccessRequest(string Reason);

public sealed record StartReviewRequest(ReviewScope Scope, Guid ReviewerId, DateOnly DueDate);

/// <summary>The Identity module: tenant-scoped users and role assignments (IAM-001), resolved per request into permissions.</summary>
public sealed class IdentityModule : IModule
{
    public string Name => "Identity";

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddIdentityInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        IdentityStaticDataEndpoints.MapStaticData(api);
        var users = api.MapGroup("/identity/users").WithTags(Name);

        users.MapPost("/", async (ProvisionUserRequest request, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                (await dispatcher.Send(new ProvisionUser(request.Username, request.DisplayName, request.IdpCode, request.ExternalSubject, request.MfaEnrolled), ct))
                .ToHttp(id =>
                {
                    Concurrency.SetETag(http, 1);
                    return Results.Created($"/api/v1/identity/users/{id}", new { id });
                }))
            .WithName("ProvisionUser")
            .WithSummary("Provision a user")
            .WithDescription("The user is Invited until the first request with a token for the external subject.")
            .RequirePermission(IdentityPermissions.UserCreate)
            .RequireIdempotencyKey();

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
            .WithName("ListUsers")
            .WithSummary("List users")
            .RequirePermission(IdentityPermissions.UserRead);

        users.MapGet("/{id:guid}", async (Guid id, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                (await dispatcher.Query(new GetUser(id), ct)).ToHttp(user =>
                {
                    Concurrency.SetETag(http, user.RowVersion);
                    return Results.Ok(user);
                }))
            .WithName("GetUser")
            .WithSummary("Get a user with its role assignments")
            .RequirePermission(IdentityPermissions.UserRead);

        users.MapPost("/{id:guid}/assign-role", (Guid id, AssignRoleRequest request, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                Versioned(http, version => dispatcher.Send(new AssignRole(id, request.RoleCode, request.ValidFrom, request.ValidTo, request.ScopeProducts,
                    request.ScopeBranches, version), ct)))
            .WithName("AssignRole")
            .WithSummary("Assign a role")
            .WithDescription("Refused for roles with no version in force, MFA-required roles without MFA (IAM-003) and segregation-of-duties conflicts (SRS-002).")
            .RequirePermission(IdentityPermissions.UserUpdate);

        users.MapPost("/{id:guid}/revoke-role", (Guid id, RevokeRoleRequest request, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                Versioned(http, version => dispatcher.Send(new RevokeRole(id, request.RoleCode, version), ct)))
            .WithName("RevokeRole")
            .WithSummary("Revoke a role")
            .WithDescription("Takes effect on the user's next request (IAM-005).")
            .RequirePermission(IdentityPermissions.UserUpdate);

        MapAccess(api.MapGroup("/identity").WithTags(Name));

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
            .WithName("ListPrivilegedAccess")
            .WithSummary("List privileged access requests and grants")
            .RequirePermission(IdentityPermissions.PrivilegedAccessRead);

        grants.MapGet("/{id:guid}", async (Guid id, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                (await dispatcher.Query(new GetGrant(id), ct)).ToHttp(grant =>
                {
                    Concurrency.SetETag(http, grant.RowVersion);
                    return Results.Ok(grant);
                }))
            .WithName("GetPrivilegedAccess")
            .WithSummary("Get a privileged access request or grant")
            .RequirePermission(IdentityPermissions.PrivilegedAccessRead);

        grants.MapPost("/{id:guid}/approve", (Guid id, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                Versioned(http, version => dispatcher.Send(new ApproveAccess(id, version), ct)))
            .WithName("ApproveAccess")
            .WithSummary("Approve privileged access (not by the requester)")
            .RequirePermission(IdentityPermissions.PrivilegedAccessApprove);

        grants.MapPost("/{id:guid}/reject", (Guid id, RejectAccessRequest request, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                Versioned(http, version => dispatcher.Send(new RejectAccess(id, request.Reason, version), ct)))
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
            .WithName("ListAccessReviews")
            .WithSummary("List access reviews")
            .RequirePermission(IdentityPermissions.AccessReviewRead);

        reviews.MapGet("/{id:guid}", async (Guid id, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                (await dispatcher.Query(new GetReview(id), ct)).ToHttp(review =>
                {
                    Concurrency.SetETag(http, review.RowVersion);
                    return Results.Ok(review);
                }))
            .WithName("GetAccessReview")
            .WithSummary("Get an access review with its items")
            .RequirePermission(IdentityPermissions.AccessReviewRead);

        reviews.MapPost("/{id:guid}/items/{itemId:guid}/confirm", (Guid id, Guid itemId, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                Versioned(http, version => dispatcher.Send(new DecideReviewItem(id, itemId, Confirm: true, version), ct)))
            .WithName("ConfirmAccess")
            .WithSummary("Confirm a reviewed access")
            .RequirePermission(IdentityPermissions.AccessReviewUpdate);

        reviews.MapPost("/{id:guid}/items/{itemId:guid}/revoke", (Guid id, Guid itemId, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                Versioned(http, version => dispatcher.Send(new DecideReviewItem(id, itemId, Confirm: false, version), ct)))
            .WithName("RevokeAccess")
            .WithSummary("Revoke a reviewed access")
            .WithDescription("The role ends when the review completes (IAM-005).")
            .RequirePermission(IdentityPermissions.AccessReviewUpdate);

        reviews.MapPost("/{id:guid}/complete", (Guid id, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                Versioned(http, version => dispatcher.Send(new CompleteReview(id, version), ct)))
            .WithName("CompleteReview")
            .WithSummary("Complete an access review")
            .WithDescription("Every item must be confirmed or revoked first. Revoked roles end at once (IAM-005).")
            .RequirePermission(IdentityPermissions.AccessReviewUpdate);
    }

    private static void MapStatus(RouteGroupBuilder users, string action, UserStatusAction statusAction, string summary) =>
        users.MapPost($"/{{id:guid}}/{action}", (Guid id, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                Versioned(http, version => dispatcher.Send(new ChangeUserStatus(id, statusAction, version), ct)))
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
}
