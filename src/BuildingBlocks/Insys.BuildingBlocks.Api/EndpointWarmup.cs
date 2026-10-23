using System.Diagnostics;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Insys.BuildingBlocks.Api;

/// <summary>
/// Whether the API's endpoints are built. The first read of the endpoint data sources compiles every Minimal API request
/// delegate (about 2,300, most of them generated static data endpoints), which takes seconds of CPU; until it is done a
/// new instance answers its first request late. Warm-up does it at startup, and readiness waits for it.
/// </summary>
public sealed class EndpointWarmup
{
    private readonly TaskCompletionSource<TimeSpan> _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly long _started = Stopwatch.GetTimestamp();

    /// <summary>Completes, once every endpoint is built, with the time from host start (when this is created) until then.</summary>
    public Task<TimeSpan> Completed => _completed.Task;

    public bool IsComplete => _completed.Task.IsCompletedSuccessfully;

    internal void Complete() => _completed.TrySetResult(Stopwatch.GetElapsedTime(_started));

    internal void Fail(Exception exception) => _completed.TrySetException(exception);
}

/// <summary>Builds every endpoint once at startup, off the startup path, and records how long it took.</summary>
public sealed class EndpointWarmupService(EndpointWarmup warmup, IEnumerable<EndpointDataSource> sources, IHostApplicationLifetime lifetime,
    ILogger<EndpointWarmupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The endpoint data sources are connected when the server builds its pipeline, after the hosted services start:
        // read them only once the application has started, or there is nothing to build yet.
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (lifetime.ApplicationStarted.Register(() => started.TrySetResult()))
        await using (stoppingToken.Register(() => started.TrySetCanceled()))
        {
            await started.Task;
        }

        try
        {
            var count = sources.Sum(source => source.Endpoints.Count);
            if (count == 0)
            {
                throw new InvalidOperationException("No endpoints were found to build.");
            }

            warmup.Complete();
            logger.LogInformation("{EndpointCount} endpoints built; ready {ElapsedMilliseconds} ms after host start", count,
                Math.Round((await warmup.Completed).TotalMilliseconds));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            warmup.Fail(ex);
            logger.LogError("Endpoint warm-up failed: {ExceptionType}", ex.GetType().Name);
        }
    }
}

/// <summary>Readiness: Unhealthy until the endpoints are built, so a new instance never takes traffic cold.</summary>
public sealed class EndpointWarmupHealthCheck(EndpointWarmup warmup) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(warmup.Completed switch
        {
            { IsCompletedSuccessfully: true } done => HealthCheckResult.Healthy($"Endpoints built {done.Result.TotalMilliseconds:F0} ms after host start.",
                new Dictionary<string, object> { ["warmupMilliseconds"] = Math.Round(done.Result.TotalMilliseconds) }),
            { IsFaulted: true } => HealthCheckResult.Unhealthy("Endpoint warm-up failed."),
            _ => HealthCheckResult.Unhealthy("Endpoints are still being built."),
        });
}
