using System.Security.Claims;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Insys.BuildingBlocks.Api;

public static class InsysClaims
{
    /// <summary>The tenant claim. The tenant is taken from this claim and nothing else (TEN-002).</summary>
    public const string TenantId = "tenant_id";

    /// <summary>
    /// Permission codes from the IAM permission catalogue (iam.ref_permission_catalogue), one claim per permission.
    /// TODO(spec): Phase 1 resolves permissions from role assignments; the IdP claim layout is an SRS §30 decision.
    /// </summary>
    public const string Permission = "permission";

    public const string Subject = "sub";
}

/// <summary>The caller must have a tenant from the principal.</summary>
public sealed class TenantRequirement : IAuthorizationRequirement;

/// <summary>The caller must hold the permission (IAM-007: enforced in the back end).</summary>
public sealed record PermissionRequirement(string Permission) : IAuthorizationRequirement;

public sealed class TenantRequirementHandler(ITenantContext tenant) : AuthorizationHandler<TenantRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, TenantRequirement requirement)
    {
        if (tenant.HasTenant)
        {
            context.Succeed(requirement);
        }
        else
        {
            context.Fail(new AuthorizationFailureReason(this, ErrorCodes.TenantContextMissing));
        }

        return Task.CompletedTask;
    }
}

public sealed class PermissionRequirementHandler(IExecutionContext execution) : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (execution.HasPermission(requirement.Permission))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Builds "perm:{code}" policies on demand: authenticated, with a tenant, holding the permission.</summary>
public sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options) : DefaultAuthorizationPolicyProvider(options)
{
    public const string Prefix = "perm:";

    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (!policyName.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return await base.GetPolicyAsync(policyName);
        }

        return new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new TenantRequirement(), new PermissionRequirement(policyName[Prefix.Length..]))
            .Build();
    }
}

/// <summary>Writes 401 and 403 as ProblemDetails with stable codes.</summary>
public sealed class ProblemAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Succeeded)
        {
            await _default.HandleAsync(next, context, policy, authorizeResult);
            return;
        }

        if (authorizeResult.Challenged)
        {
            await Write(context, StatusCodes.Status401Unauthorized, ErrorCodes.Unauthenticated, "Authentication is required.");
            return;
        }

        var tenantMissing = authorizeResult.AuthorizationFailure?.FailureReasons.Any(r => r.Message == ErrorCodes.TenantContextMissing) == true;
        await Write(context, StatusCodes.Status403Forbidden,
            tenantMissing ? ErrorCodes.TenantContextMissing : ErrorCodes.Forbidden,
            tenantMissing ? "No tenant is established for the caller." : "The caller does not hold the required permission.");
    }

    private static Task Write(HttpContext context, int status, string code, string message) =>
        Problems.For(code, message, status).ExecuteAsync(context);
}

public static class EndpointSecurityExtensions
{
    /// <summary>Declares the endpoint's permission policy. Every endpoint must declare one.</summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(PermissionPolicyProvider.Prefix + permission)
            .WithMetadata(new RequiredPermissionMetadata(permission));
}

/// <summary>Endpoint metadata naming the permission, used by OpenAPI and the tests that check every endpoint has one.</summary>
public sealed record RequiredPermissionMetadata(string Permission);

/// <summary>
/// Initialises the request's <see cref="ExecutionScope"/> from the authenticated principal only. Route values,
/// query strings, headers and bodies never set the tenant (TEN-002, DBA-003).
/// </summary>
public sealed class ExecutionScopeMiddleware(RequestDelegate next)
{
    public const string CorrelationHeader = "X-Correlation-Id";

    public Task InvokeAsync(HttpContext context, ExecutionScope scope)
    {
        var user = context.User;
        Guid? tenantId = user.Identity?.IsAuthenticated == true && Guid.TryParse(user.FindFirstValue(InsysClaims.TenantId), out var parsed) && parsed != Guid.Empty
            ? parsed
            : null;
        var actor = user.FindFirstValue(InsysClaims.Subject) ?? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous";
        var permissions = user.FindAll(InsysClaims.Permission).Select(c => c.Value);
        Guid? correlation = Guid.TryParse(context.Request.Headers[CorrelationHeader], out var c) ? c : null;
        scope.InitialiseForUser(tenantId, actor, permissions, correlation);
        context.Response.Headers[CorrelationHeader] = scope.CorrelationId.ToString();
        return next(context);
    }
}
