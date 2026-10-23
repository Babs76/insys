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
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Insys.BuildingBlocks.Api;

/// <summary>
/// Cross-origin access for the front end's development server (GAP-002), from Cors:AllowedOrigins
/// (http://localhost:5173 in appsettings.Development.json). Only Development and Testing honour it; production serves
/// the front end from the same origin through the gateway, and refuses to start if origins are configured.
/// </summary>
public static class DevelopmentCors
{
    public const string Policy = "development-cors";

    /// <summary>The headers a browser may read from responses: the ones the front end uses.</summary>
    public static readonly string[] ExposedHeaders = ["ETag", "Location", "Content-Language", "Retry-After", "X-Correlation-Id"];

    public static readonly string[] AllowedHeaders = ["Authorization", "Content-Type", "Accept", "Accept-Language", "If-Match", "Idempotency-Key", "X-Correlation-Id"];

    public static string[] Origins(IConfiguration configuration) =>
        configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()?.Where(o => !string.IsNullOrWhiteSpace(o)).ToArray() ?? [];

    public static bool Allowed(IHostEnvironment environment) => environment.IsDevelopment() || environment.IsEnvironment("Testing");

    public static void Configure(CorsPolicyBuilder policy, IConfiguration configuration, IHostEnvironment environment)
    {
        var origins = Allowed(environment) ? Origins(configuration) : [];
        if (origins.Length == 0)
        {
            return;
        }

        policy.WithOrigins(origins).WithMethods("GET", "POST", "PUT", "PATCH", "DELETE").WithHeaders(AllowedHeaders).WithExposedHeaders(ExposedHeaders)
            .SetPreflightMaxAge(TimeSpan.FromMinutes(10));
    }
}

/// <summary>Refuses to start with Cors:AllowedOrigins configured outside Development and Testing: production is same-origin.</summary>
public sealed class DevelopmentCorsGuard(IConfiguration configuration, IHostEnvironment environment) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        DevelopmentCors.Origins(configuration).Length > 0 && !DevelopmentCors.Allowed(environment)
            ? throw new InvalidOperationException($"Cors:AllowedOrigins is configured in the {environment.EnvironmentName} environment. Cross-origin access is for "
                + "development only; production serves the front end from the same origin.")
            : Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
