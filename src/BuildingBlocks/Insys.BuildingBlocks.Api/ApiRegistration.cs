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
using System.Text;
using System.Threading.RateLimiting;
using Asp.Versioning;
using Insys.BuildingBlocks.Application.Localisation;
using Insys.BuildingBlocks.Domain;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;

namespace Insys.BuildingBlocks.Api;

public sealed class InsysAuthenticationOptions
{
    /// <summary>The OIDC issuer: the Keycloak realm (ADR-017), for example http://localhost:8080/realms/insys locally.</summary>
    public string? Authority { get; set; }

    public string? Audience { get; set; }

    public string? Issuer { get; set; }

    /// <summary>Whether the issuer's metadata must be fetched over HTTPS; false only for a local Keycloak on http://localhost.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>
    /// A symmetric (HS256) signing key for local development and tests only. Ignored when Authority is set, and accepted
    /// only in the environments of <see cref="DevelopmentKeyEnvironments"/>: anywhere else the host refuses to start.
    /// </summary>
    public string? DevelopmentSigningKey { get; set; }

    /// <summary>The explicit allow-list of environments in which the development signing key may be used.</summary>
    public static readonly IReadOnlyList<string> DevelopmentKeyEnvironments = ["Development", "Testing"];

    public static bool AllowsDevelopmentKey(IHostEnvironment environment) =>
        DevelopmentKeyEnvironments.Any(name => string.Equals(environment.EnvironmentName, name, StringComparison.Ordinal));
}

/// <summary>Refuses to start a host that has a development signing key configured outside the allow-list.</summary>
public sealed class DevelopmentSigningKeyGuard(IConfiguration configuration, IHostEnvironment environment) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var key = configuration["Authentication:DevelopmentSigningKey"];
        return !string.IsNullOrWhiteSpace(key) && !InsysAuthenticationOptions.AllowsDevelopmentKey(environment)
            ? throw new InvalidOperationException(
                $"Authentication:DevelopmentSigningKey is configured in the {environment.EnvironmentName} environment. It is allowed only in "
                + $"{string.Join(" and ", InsysAuthenticationOptions.DevelopmentKeyEnvironments)}; remove it and configure Authentication:Authority.")
            : Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class ApiRegistration
{

    public static IServiceCollection AddInsysApi(this IServiceCollection services)
    {
        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            context.ProblemDetails.Instance ??= context.HttpContext.Request.Path;
            context.ProblemDetails.Extensions.TryAdd("traceId", context.HttpContext.TraceIdentifier);
            context.ProblemDetails.Extensions["correlationId"] = ErrorCorrelation.Ensure(context.HttpContext);
            if (!context.ProblemDetails.Extensions.ContainsKey("code") && ErrorContract.CodeFor(context.ProblemDetails.Status ?? context.HttpContext.Response.StatusCode) is { } code)
            {
                context.ProblemDetails.Extensions["code"] = code;
            }

            ProblemLocalisation.Apply(context.HttpContext, context.ProblemDetails);
        });
        services.AddScoped<RequestLanguage>();
        services.AddExceptionHandler<InsysExceptionHandler>();
        services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

        // Bound when first used, so configuration added after registration (tests, environment overrides) applies.
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddHostedService<DevelopmentSigningKeyGuard>();

        // Development CORS for the front end's dev server (GAP-002): origins from Cors:AllowedOrigins, honoured only in
        // Development and Testing. Production is same-origin through the gateway; there, configured origins stop startup.
        services.AddCors();
        services.AddOptions<Microsoft.AspNetCore.Cors.Infrastructure.CorsOptions>().Configure<IConfiguration, Microsoft.Extensions.Hosting.IHostEnvironment>(
            (options, config, environment) => options.AddPolicy(DevelopmentCors.Policy, policy => DevelopmentCors.Configure(policy, config, environment)));
        services.AddHostedService<DevelopmentCorsGuard>();
        services.AddSingleton<EndpointWarmup>();
        services.AddSingleton<Application.Context.IChannelScopedTransactions, EndpointChannelScopedTransactions>();
        services.AddHealthChecks().AddCheck<EndpointWarmupHealthCheck>("endpoints", tags: ["ready"]);
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme).Configure<IConfiguration, IHostEnvironment>((options, config, environment) =>
        {
            var auth = config.GetSection("Authentication").Get<InsysAuthenticationOptions>() ?? new InsysAuthenticationOptions();
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = auth.Issuer ?? auth.Authority,
                ValidateAudience = true,
                ValidAudience = auth.Audience,
                ValidateLifetime = true,
                NameClaimType = InsysClaims.Subject,
            };
            if (!string.IsNullOrWhiteSpace(auth.Authority))
            {
                options.Authority = auth.Authority;
                options.RequireHttpsMetadata = auth.RequireHttpsMetadata;
            }
            else if (!string.IsNullOrWhiteSpace(auth.DevelopmentSigningKey) && InsysAuthenticationOptions.AllowsDevelopmentKey(environment))
            {
                options.TokenValidationParameters.IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(auth.DevelopmentSigningKey));
            }
        });

        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddScoped<IAuthorizationHandler, TenantRequirementHandler>();
        services.AddScoped<IAuthorizationHandler, PermissionRequirementHandler>();
        services.AddScoped<IAuthorizationHandler, PlatformOperatorRequirementHandler>();
        services.AddHttpContextAccessor();
        services.AddScoped<IAuthorizationHandler, TenantMemberRequirementHandler>();
        services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler, ProblemAuthorizationResultHandler>();
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().AddRequirements(new TenantRequirement()).Build());

        // TODO(spec): rate limits are not specified; NFR targets are tenant-level. Partitioned per tenant, or per client IP without one.
        services.AddRateLimiter(_ => { });
        services.AddOptions<Microsoft.AspNetCore.RateLimiting.RateLimiterOptions>().Configure<IConfiguration>((options, config) =>
        {
            var permitsPerMinute = config.GetValue("RateLimiting:PermitsPerMinute", 1200);
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, _) =>
            {
                // Retry-After (GAP-006): when the limiter's window lets the tenant in again, else the window's length.
                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait) ? wait : TimeSpan.FromMinutes(1);
                context.HttpContext.Response.Headers.RetryAfter =
                    Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                return new ValueTask(Problems.For(ErrorCodes.RateLimited, "Too many requests. Retry later.", StatusCodes.Status429TooManyRequests)
                    .ExecuteAsync(context.HttpContext));
            };
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.User.FindFirst(InsysClaims.TenantId)?.Value ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = permitsPerMinute, Window = TimeSpan.FromMinutes(1) }));
        });

        services.AddApiVersioning(options =>
            {
                options.DefaultApiVersion = new ApiVersion(1);
                options.ReportApiVersions = true;
                options.ApiVersionReader = new UrlSegmentApiVersionReader();
            })
            .AddApiExplorer(options =>
            {
                options.GroupNameFormat = "'v'V";
                options.SubstituteApiVersionInUrl = true;
            });

        foreach (var (name, title, description) in ApiDocuments.All)
        {
            services.AddOpenApi(name, options => ConfigureDocument(options, name, title, description));
        }

        return services;
    }

    private static void ConfigureDocument(Microsoft.AspNetCore.OpenApi.OpenApiOptions options, string name, string title, string description)
    {
        // Each endpoint belongs to exactly one document (ApiDocuments.For), whatever its API version group.
        options.ShouldInclude = api => ApiDocuments.For(api.ActionDescriptor.EndpointMetadata) == name;
        options.CreateSchemaReferenceId = SchemaNames.For;
        options.AddSchemaTransformer(StateEnums.Apply);
        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info = new OpenApiInfo
            {
                Title = title,
                Version = "v1",
                Description = description + " Every response carries X-Correlation-Id: the caller's when it sent a valid one, else a new one; errors repeat it as correlationId.",
            };

            // No servers: the document must be the same wherever it is generated (build time, tests, runtime).
            document.Servers = [];
            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes["bearer"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "OIDC access token carrying sub and tenant_id. Permissions come from IAM role assignments.",
            };
            document.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("bearer", document)] = [] }];
            ErrorContract.Apply(document);
            return Task.CompletedTask;
        });
        options.AddOperationTransformer((operation, context, _) =>
        {
            var metadata = context.Description.ActionDescriptor.EndpointMetadata;
            var permission = metadata.OfType<RequiredPermissionMetadata>().FirstOrDefault();
            if (permission is not null)
            {
                operation.Extensions ??= new Dictionary<string, Microsoft.OpenApi.IOpenApiExtension>();
                operation.Extensions["x-permission"] = new Microsoft.OpenApi.JsonNodeExtension(System.Text.Json.Nodes.JsonValue.Create(permission.Permission));
            }

            if (operation.OperationId is "ListAuditEvents" or "GetRecordHistory"
                && operation.Parameters?.FirstOrDefault(p => p.Name == "targetType")?.Schema is OpenApiSchema targetType)
            {
                // GAP-029: the target types audit events name.
                targetType.Enum = [.. AuditTargetTypes.All().Select(t => (System.Text.Json.Nodes.JsonNode)System.Text.Json.Nodes.JsonValue.Create(t))];
            }

            foreach (var known in metadata.OfType<KnownValuesMetadata>())
            {
                if (operation.Parameters?.FirstOrDefault(p => p.Name == known.Parameter) is OpenApiParameter parameter)
                {
                    parameter.Extensions ??= new Dictionary<string, Microsoft.OpenApi.IOpenApiExtension>();
                    parameter.Extensions[KnownValuesExtensions.Extension] = new Microsoft.OpenApi.JsonNodeExtension(
                        new System.Text.Json.Nodes.JsonArray([.. known.Values.Select(v => (System.Text.Json.Nodes.JsonNode)System.Text.Json.Nodes.JsonValue.Create(v))]));
                    parameter.Description = known.Description;
                }
            }

            if (metadata.OfType<EventualEffectsMetadata>().FirstOrDefault() is { } eventual)
            {
                operation.Extensions ??= new Dictionary<string, Microsoft.OpenApi.IOpenApiExtension>();
                operation.Extensions[EventualEffectsExtensions.Extension] = new Microsoft.OpenApi.JsonNodeExtension(System.Text.Json.Nodes.JsonValue.Create(eventual.Effects));
                operation.Description = (operation.Description is { Length: > 0 } d ? d + " " : "") + "Completes asynchronously (GAP-027): " + eventual.Effects;
            }

            if (metadata.OfType<IfMatchRequiredMetadata>().Any())
            {
                operation.Parameters ??= [];
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = "If-Match",
                    In = ParameterLocation.Header,
                    Required = true,
                    Description = "The ETag from the last GET (row_version, DBA-011). Missing gives 428; stale gives 412.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String },
                });
            }

            if (metadata.OfType<IdempotentEndpointMetadata>().Any())
            {
                operation.Parameters ??= [];
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = IdempotencyFilter.HeaderName,
                    In = ParameterLocation.Header,
                    Required = true,
                    Description = "A unique key for this operation. A retry with the same key returns the original response.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String, MaxLength = 80 },
                });
            }

            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Adds the middleware in order: the warm-up gate, routing, errors, authentication, execution scope, rate limiting,
    /// authorisation. The gate comes before routing, which builds the router on the first request it sees.
    /// </summary>
    public static WebApplication UseInsysApi(this WebApplication app)
    {
        app.UseMiddleware<WarmupGateMiddleware>();
        app.UseRouting();
        app.UseCors(DevelopmentCors.Policy);
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseAuthentication();
        app.UseMiddleware<ExecutionScopeMiddleware>();
        app.UseRateLimiter();
        app.UseAuthorization();
        app.UseMiddleware<IdempotencyBufferingMiddleware>();
        return app;
    }

    /// <summary>
    /// Health endpoints: liveness without dependencies; readiness with the database, outbox lag and partitions; and
    /// /health with every check and its details (status, description and data per check).
    /// </summary>
    public static WebApplication MapInsysHealth(this WebApplication app)
    {
        app.MapHealthChecks("/health", new HealthCheckOptions { ResponseWriter = WriteHealthReport }).AllowAnonymous().DisableRateLimiting();
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous().DisableRateLimiting();
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") }).AllowAnonymous().DisableRateLimiting();
        return app;
    }

    private static Task WriteHealthReport(HttpContext context, Microsoft.Extensions.Diagnostics.HealthChecks.HealthReport report)
    {
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(e => e.Key, e => new
            {
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                data = e.Value.Data,
            }),
        });
    }

    /// <summary>The versioned group /api/v1 that modules map their endpoints onto.</summary>
    public static IEndpointRouteBuilder MapInsysV1(this WebApplication app)
    {
        var api = app.NewVersionedApi("Insys");
        var v1 = api.MapGroup("/api/v{version:apiVersion}").HasApiVersion(new ApiVersion(1));
        ((IEndpointConventionBuilder)v1).Finally(StandardProblems.Declare);
        return v1;
    }
}
