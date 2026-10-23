using System.Security.Claims;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Localisation;
using Insys.BuildingBlocks.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Insys.BuildingBlocks.Api;

public static class InsysClaims
{
    /// <summary>The tenant claim. The tenant is taken from this claim and nothing else (TEN-002).</summary>
    public const string TenantId = "tenant_id";

    // Access tokens carry only sub and tenant_id (OQ-014 resolved). Permissions are resolved per request from IAM
    // role assignments by IPrincipalResolver, so token claims never grant permissions.

    public const string Subject = "sub";
}

/// <summary>The caller must have a tenant from the principal.</summary>
public sealed class TenantRequirement : IAuthorizationRequirement;

/// <summary>The caller must hold the permission (IAM-007: enforced in the back end).</summary>
public sealed record PermissionRequirement(string Permission) : IAuthorizationRequirement;

/// <summary>The caller must belong to the platform operator tenant (platform administration, TEN-007).</summary>
public sealed class PlatformOperatorRequirement : IAuthorizationRequirement;

public sealed class PlatformOperatorRequirementHandler(ITenantContext tenant, IOptions<PlatformOptions> platform) : AuthorizationHandler<PlatformOperatorRequirement>
{
    public const string FailureReason = "PLATFORM_OPERATOR_REQUIRED";

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PlatformOperatorRequirement requirement)
    {
        var operatorTenant = platform.Value.OperatorTenantId;
        if (operatorTenant != Guid.Empty && tenant.TenantId == operatorTenant)
        {
            context.Succeed(requirement);
        }
        else
        {
            context.Fail(new AuthorizationFailureReason(this, FailureReason));
        }

        return Task.CompletedTask;
    }
}

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

/// <summary>
/// Builds policies on demand: "perm:{code}" (authenticated, with a tenant, holding the permission) and
/// "platform:{code}" (the same, and the tenant is the platform operator).
/// </summary>
public sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options) : DefaultAuthorizationPolicyProvider(options)
{
    public const string Prefix = "perm:";
    public const string PlatformPrefix = "platform:";

    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (policyName.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new TenantRequirement(), new PermissionRequirement(policyName[Prefix.Length..]))
                .Build();
        }

        if (policyName.StartsWith(PlatformPrefix, StringComparison.Ordinal))
        {
            return new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new TenantRequirement(), new PlatformOperatorRequirement(), new PermissionRequirement(policyName[PlatformPrefix.Length..]))
                .Build();
        }

        return await base.GetPolicyAsync(policyName);
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
        await RecordDenial(context, policy);
        await Write(context, StatusCodes.Status403Forbidden,
            tenantMissing ? ErrorCodes.TenantContextMissing : ErrorCodes.Forbidden,
            tenantMissing ? "No tenant is established for the caller." : "The caller does not hold the required permission.");
    }

    /// <summary>Audits a denied request in the caller's tenant (IAM-006, TEN-005). Never blocks the response.</summary>
    private static async Task RecordDenial(HttpContext context, AuthorizationPolicy policy)
    {
        var execution = context.RequestServices.GetRequiredService<ExecutionScope>();
        var sink = context.RequestServices.GetService<IAuditSink>();
        if (sink is null || execution.TenantId is not { } tenantId)
        {
            return;
        }

        var required = string.Join(",", policy.Requirements.OfType<PermissionRequirement>().Select(r => r.Permission));
        try
        {
            await sink.Record(new AuditRecord(tenantId, execution.ActorId, "USER",
                new AuditEntry("ACCESS", "AccessDenied", "Endpoint", $"{context.Request.Method} {context.Request.Path}",
                    new Dictionary<string, string?> { ["requiredPermission"] = required }),
                AuditOutcome.Denied, DateTimeOffset.UtcNow, execution.CorrelationId), context.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.RequestServices.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ProblemAuthorizationResultHandler>>()
                .LogWarning("Could not audit a denied request: {ExceptionType}", ex.GetType().Name);
        }
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

    /// <summary>A platform administration endpoint: the platform operator tenant only, holding the permission.</summary>
    public static TBuilder RequirePlatformPermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(PermissionPolicyProvider.PlatformPrefix + permission)
            .WithMetadata(new RequiredPermissionMetadata(permission), new PlatformOperatorEndpointMetadata());
}

/// <summary>Endpoint metadata naming the permission, used by OpenAPI and the tests that check every endpoint has one.</summary>
public sealed record RequiredPermissionMetadata(string Permission);

/// <summary>Marks a platform administration endpoint.</summary>
public sealed record PlatformOperatorEndpointMetadata;

/// <summary>
/// Initialises the request's <see cref="ExecutionScope"/> from the authenticated principal only. Route values,
/// query strings, headers and bodies never set the tenant (TEN-002, DBA-003). The subject is resolved to an IAM user
/// and its current permissions on every request (OQ-014, IAM-005); an unknown, locked or disabled user has none.
/// </summary>
public sealed class ExecutionScopeMiddleware(RequestDelegate next)
{
    public const string CorrelationHeader = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context, ExecutionScope scope)
    {
        var user = context.User;
        var authenticated = user.Identity?.IsAuthenticated == true;
        Guid? tenantId = authenticated && Guid.TryParse(user.FindFirstValue(InsysClaims.TenantId), out var parsed) && parsed != Guid.Empty
            ? parsed
            : null;
        var subject = user.FindFirstValue(InsysClaims.Subject) ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
        var actor = subject ?? "anonymous";
        IEnumerable<string> permissions = [];
        Guid? memberOf = null;
        if (tenantId is not null && subject is not null && context.RequestServices.GetService<IPrincipalResolver>() is { } resolver)
        {
            var principal = await resolver.Resolve(tenantId.Value, subject, context.RequestAborted);
            if (principal is not null)
            {
                actor = principal.UserId.ToString();
                permissions = principal.Permissions;
                memberOf = tenantId;
            }
        }

        // The request language comes from the tenant's languages only for one of its own users (addendum §2), so a
        // token for another tenant learns nothing about that tenant.
        await RequestLanguageResolver.Initialise(context, memberOf);

        Guid? correlation = Guid.TryParse(context.Request.Headers[CorrelationHeader], out var c) ? c : null;
        scope.InitialiseForUser(tenantId, actor, permissions, correlation);
        context.Response.Headers[CorrelationHeader] = scope.CorrelationId.ToString();
        await next(context);
    }
}

/// <summary>
/// Resolves the request language (docs/specs/addenda/localisation.md §2, OQ-068) and loads the message catalogue texts of
/// its fallback chain; every /api response carries Content-Language, which a localised ProblemDetails may override.
/// </summary>
public static class RequestLanguageResolver
{
    public static async Task Initialise(HttpContext context, Guid? tenantId)
    {
        if (context.RequestServices.GetService<RequestLanguage>() is not { } request)
        {
            return;
        }

        var tenant = tenantId is { } id && context.RequestServices.GetService<ILanguageSettings>() is { } settings
            ? await settings.For(id, context.RequestAborted)
            : TenantLanguages.EnglishOnly;
        var language = LanguageChain.Resolve(LanguageChain.Parse(context.Request.Headers.AcceptLanguage), tenant);
        var chain = LanguageChain.Chain(language, tenant);
        var messages = new List<(string, MessageTexts)>();
        if (context.RequestServices.GetService<IMessageCatalogue>() is { } catalogue)
        {
            foreach (var each in chain)
            {
                messages.Add((each, await catalogue.Texts(tenantId, each, context.RequestAborted)));
            }
        }

        request.Initialise(language, chain, messages);
        context.Response.OnStarting(() =>
        {
            if (context.Request.Path.StartsWithSegments("/api") && !context.Response.Headers.ContainsKey(HeaderNames.ContentLanguage))
            {
                context.Response.Headers.ContentLanguage = request.Language;
            }

            return Task.CompletedTask;
        });
    }
}
