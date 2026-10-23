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
using System.Diagnostics;
using System.Globalization;
using Insys.BuildingBlocks.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Insys.BuildingBlocks.Api;

/// <summary>
/// Whether the router is built. Routing builds its matcher on the first request, compiling every Minimal API request
/// delegate (about 2,300, most of them generated static data endpoints) synchronously, and every other request waits
/// behind it; on a busy two-core machine that took longer than a client's timeout. The warm-up gate builds it once at
/// startup and holds requests off until then.
/// </summary>
public sealed class EndpointWarmup
{
    private readonly TaskCompletionSource<TimeSpan> _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly long _started = Stopwatch.GetTimestamp();

    /// <summary>Completes, once the router is built, with the time from host start (when this is created) until then.</summary>
    public Task<TimeSpan> Completed => _completed.Task;

    public bool IsComplete => _completed.Task.IsCompletedSuccessfully;

    internal void Complete() => _completed.TrySetResult(Stopwatch.GetElapsedTime(_started));

    internal void Fail(Exception exception) => _completed.TrySetException(exception);
}

/// <summary>
/// The first middleware, ahead of routing. When the application has started it sends one internal liveness request
/// through the rest of the pipeline, which builds the real router once. Until that completes, liveness is answered here
/// (the process is alive) and every other request gets an immediate 503 NOT_READY with Retry-After, never a wait.
/// </summary>
public sealed class WarmupGateMiddleware
{
    public const string LivenessPath = "/health/live";
    private const int RetryAfterSeconds = 2;
    private static readonly object WarmupRequest = new();

    private readonly RequestDelegate _next;
    private readonly EndpointWarmup _warmup;

    public WarmupGateMiddleware(RequestDelegate next, EndpointWarmup warmup, IServiceScopeFactory scopes, IHostApplicationLifetime lifetime,
        ILogger<WarmupGateMiddleware> logger)
    {
        _next = next;
        _warmup = warmup;
        lifetime.ApplicationStarted.Register(() => _ = Task.Run(() => Warm(scopes, logger)));
    }

    public Task InvokeAsync(HttpContext context)
    {
        if (_warmup.IsComplete || context.Items.ContainsKey(WarmupRequest))
        {
            return _next(context);
        }

        if (context.Request.Path.Equals(LivenessPath, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/plain";
            return context.Response.WriteAsync("Healthy");
        }

        context.Response.Headers.RetryAfter = RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        return Problems.For(ErrorCodes.NotReady, "The instance is starting; retry shortly.", StatusCodes.Status503ServiceUnavailable).ExecuteAsync(context);
    }

    private async Task Warm(IServiceScopeFactory scopes, ILogger logger)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            context.Items[WarmupRequest] = true;
            context.Request.Method = HttpMethods.Get;
            context.Request.Path = LivenessPath;
            context.Response.Body = Stream.Null;
            await _next(context);
            _warmup.Complete();
            logger.LogInformation("Router built; ready {ElapsedMilliseconds} ms after host start", Math.Round((await _warmup.Completed).TotalMilliseconds));
        }
        catch (Exception ex)
        {
            _warmup.Fail(ex);
            logger.LogError("Router warm-up failed: {ExceptionType}", ex.GetType().Name);
        }
    }
}

/// <summary>Readiness: Unhealthy until the router is built, so a new instance never takes traffic cold.</summary>
public sealed class EndpointWarmupHealthCheck(EndpointWarmup warmup) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(warmup.Completed switch
        {
            { IsCompletedSuccessfully: true } done => HealthCheckResult.Healthy($"Router built {done.Result.TotalMilliseconds:F0} ms after host start.",
                new Dictionary<string, object> { ["warmupMilliseconds"] = Math.Round(done.Result.TotalMilliseconds) }),
            { IsFaulted: true } => HealthCheckResult.Unhealthy("Router warm-up failed."),
            _ => HealthCheckResult.Unhealthy("The router is still being built."),
        });
}
