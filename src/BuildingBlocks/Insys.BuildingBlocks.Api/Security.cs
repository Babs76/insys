/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Api
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

    /// <summary>The OAuth client the token was issued to, which names the request's channel (F005 §1).</summary>
    public const string AuthorisedParty = "azp";
}

/// <summary>The caller must have a tenant from the principal.</summary>
public sealed class TenantRequirement : IAuthorizationRequirement;

/// <summary>The caller must hold the permission (IAM-007: enforced in the back end).</summary>
public sealed record PermissionRequirement(string Permission) : IAuthorizationRequirement;

/// <summary>The caller must belong to the platform operator tenant (platform administration, TEN-007).</summary>
public sealed class PlatformOperatorRequirement : IAuthorizationRequirement;

/// <summary>The token's subject must be an IAM user of the token's tenant (an endpoint about the caller, such as /identity/me).</summary>
public sealed class TenantMemberRequirement : IAuthorizationRequirement
{
    /// <summary>The HttpContext item ExecutionScopeMiddleware sets when the subject resolved to a user of the tenant.</summary>
    public static readonly object MemberItem = new();
}

public sealed class TenantMemberRequirementHandler(IHttpContextAccessor http) : AuthorizationHandler<TenantMemberRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, TenantMemberRequirement requirement)
    {
        if (http.HttpContext?.Items.ContainsKey(TenantMemberRequirement.MemberItem) == true)
        {
            context.Succeed(requirement);
        }
        else
        {
            context.Fail(new AuthorizationFailureReason(this, ErrorCodes.Forbidden));
        }

        return Task.CompletedTask;
    }
}

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
    public const string TenantMember = "tenant-member";

    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (policyName.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new TenantRequirement(), new PermissionRequirement(policyName[Prefix.Length..]))
                .Build();
        }

        if (policyName == TenantMember)
        {
            return new AuthorizationPolicyBuilder().RequireAuthenticatedUser().AddRequirements(new TenantRequirement(), new TenantMemberRequirement()).Build();
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

        // ADR-026 §2: the permissions come from the functions, so a missing permission is first explained by the function.
        // A caller outside the operator tenant is refused as before, whatever its functions.
        var notOperator = authorizeResult.AuthorizationFailure?.FailureReasons.Any(r => r.Message == PlatformOperatorRequirementHandler.FailureReason) == true;
        if (!tenantMissing && !notOperator && Functions.FunctionChecks.Refusal(context) is { } functionRefused)
        {
            await functionRefused.ExecuteAsync(context);
            return;
        }

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

    /// <summary>The x-permission of an endpoint about the caller: any user of the tenant, without a permission of its own.</summary>
    public const string SignedInUser = "signed-in user";

    /// <summary>An endpoint about the caller (their own profile, say): any IAM user of the token's tenant may call it, and nobody else.</summary>
    public static TBuilder RequireSignedInUser<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(PermissionPolicyProvider.TenantMember).WithMetadata(new RequiredPermissionMetadata(SignedInUser));

    /// <summary>The x-permission of a provider callback address: no token; the endpoint key and the signature authenticate (ADR-023).</summary>
    public const string SignedProviderCallback = "signed provider callback";

    /// <summary>
    /// A provider callback address (ADR-023): anonymous to the token checks, because the endpoint key in its path and the
    /// HMAC signature over the body authenticate the provider and give the tenant. The handler does those checks.
    /// </summary>
    public static TBuilder RequireSignedProviderCallback<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AllowAnonymous().WithMetadata(new RequiredPermissionMetadata(SignedProviderCallback), new SignedProviderCallbackMetadata());

    /// <summary>A platform administration endpoint: the platform operator tenant only, holding the permission.</summary>
    public static TBuilder RequirePlatformPermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(PermissionPolicyProvider.PlatformPrefix + permission)
            .WithMetadata(new RequiredPermissionMetadata(permission), new PlatformOperatorEndpointMetadata());

    /// <summary>
    /// A permission the handler checks besides the endpoint's own, and when (GAP-052): declared as x-additional-permissions
    /// so the contract names every permission the operation enforces. An endpoint may declare several.
    /// </summary>
    public static TBuilder AlsoChecks<TBuilder>(this TBuilder builder, string permission, string when)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new AdditionalPermissionMetadata(permission, when));
}

/// <summary>Endpoint metadata naming the permission, used by OpenAPI and the tests that check every endpoint has one.</summary>
public sealed record RequiredPermissionMetadata(string Permission);

/// <summary>A permission the handler checks besides the endpoint's own, and when it applies (GAP-052).</summary>
public sealed record AdditionalPermissionMetadata(string Permission, string When);

/// <summary>Marks a provider callback address authenticated by its endpoint key and signature, not a token (ADR-023).</summary>
public sealed record SignedProviderCallbackMetadata;

/// <summary>Marks a platform administration endpoint.</summary>
public sealed record PlatformOperatorEndpointMetadata;

/// <summary>
/// Initialises the request's <see cref="ExecutionScope"/> from the authenticated principal only. Route values,
/// query strings, headers and bodies never set the tenant (TEN-002, DBA-003). The subject is resolved to an IAM user
/// and its current permissions on every request (OQ-014, IAM-005); an unknown, locked or disabled user has none.
/// </summary>
public sealed class ExecutionScopeMiddleware(RequestDelegate next)
{
    private static readonly string[] SecondFactors = ["otp", "mfa", "hwk", "swk"];

    /// <summary>
    /// Whether the token shows a second factor: Keycloak's amr claim (with the "Authentication Method Reference (AMR)"
    /// mapper) naming one, or an acr level of 2 or above (step-up authentication levels of assurance).
    /// </summary>
    private static bool MultiFactor(ClaimsPrincipal user) =>
        user.FindAll("amr").Any(c => SecondFactors.Contains(c.Value, StringComparer.OrdinalIgnoreCase))
        || (int.TryParse(user.FindFirstValue("acr"), out var level) && level >= 2);

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
        IReadOnlyDictionary<string, IReadOnlyList<PermissionGrant>>? grants = null;
        Guid? memberOf = null;
        IReadOnlyCollection<string>? roles = null;
        ExternalSubject? external = null;
        var configurationOnly = false;
        FunctionAccess? functions = null;
        if (tenantId is not null && subject is not null && context.RequestServices.GetService<IPrincipalResolver>() is { } resolver)
        {
            var principal = await resolver.Resolve(tenantId.Value, subject, context.RequestAborted);
            if (principal is not null)
            {
                actor = principal.UserId.ToString();
                permissions = principal.Permissions;
                grants = principal.Grants;
                roles = principal.RoleCodes;
                external = principal.External;
                configurationOnly = principal.ConfigurationOnly;
                functions = principal.Functions;
                memberOf = tenantId;
                context.Items[TenantMemberRequirement.MemberItem] = true;
            }
        }

        // The request language comes from the tenant's languages only for one of its own users (addendum §2), so a
        // token for another tenant learns nothing about that tenant.
        await RequestLanguageResolver.Initialise(context, memberOf);

        Guid? correlation = Guid.TryParse(context.Request.Headers[CorrelationHeader], out var c) ? c : null;
        scope.InitialiseForUser(tenantId, actor, permissions, correlation, grants, user.FindFirstValue(InsysClaims.AuthorisedParty), roles, external, MultiFactor(user),
            configurationOnly, functions);
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
