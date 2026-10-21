using System.Text;
using System.Threading.RateLimiting;
using Asp.Versioning;
using Insys.BuildingBlocks.Domain;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;

namespace Insys.BuildingBlocks.Api;

public sealed class InsysAuthenticationOptions
{
    /// <summary>The OIDC issuer. TODO(spec): the identity provider is an SRS §30 decision.</summary>
    public string? Authority { get; set; }

    public string? Audience { get; set; }

    public string? Issuer { get; set; }

    /// <summary>A symmetric signing key for local development and tests only. Ignored when Authority is set.</summary>
    public string? DevelopmentSigningKey { get; set; }
}

public static class ApiRegistration
{

    public static IServiceCollection AddInsysApi(this IServiceCollection services)
    {
        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            context.ProblemDetails.Instance ??= context.HttpContext.Request.Path;
            context.ProblemDetails.Extensions.TryAdd("traceId", context.HttpContext.TraceIdentifier);
        });
        services.AddExceptionHandler<InsysExceptionHandler>();
        services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

        // Bound when first used, so configuration added after registration (tests, environment overrides) applies.
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme).Configure<IConfiguration>((options, config) =>
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
            }
            else if (!string.IsNullOrWhiteSpace(auth.DevelopmentSigningKey))
            {
                options.TokenValidationParameters.IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(auth.DevelopmentSigningKey));
            }
        });

        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddScoped<IAuthorizationHandler, TenantRequirementHandler>();
        services.AddScoped<IAuthorizationHandler, PermissionRequirementHandler>();
        services.AddScoped<IAuthorizationHandler, PlatformOperatorRequirementHandler>();
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
                new ValueTask(Problems.For(ErrorCodes.RateLimited, "Too many requests. Retry later.", StatusCodes.Status429TooManyRequests)
                    .ExecuteAsync(context.HttpContext));
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
        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info = new OpenApiInfo { Title = title, Version = "v1", Description = description };

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

    /// <summary>Adds the middleware in order: errors, authentication, execution scope, rate limiting, authorisation.</summary>
    public static WebApplication UseInsysApi(this WebApplication app)
    {
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
        return api.MapGroup("/api/v{version:apiVersion}").HasApiVersion(new ApiVersion(1));
    }
}
