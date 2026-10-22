using System.Collections.Concurrent;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.Integration.Application.Webhooks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Insys.Integration.Infrastructure.Webhooks;

public sealed class WebhookOptions
{
    /// <summary>"http" sends; "stub" records each request and answers 200, or 503 for a host starting "fail." (tests and local runs).</summary>
    public string Transport { get; set; } = "http";

    public bool JobEnabled { get; set; } = true;

    public TimeSpan JobInterval { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>Sends webhooks over HTTPS with the signature headers; a timeout or connection failure is an attempt without a response.</summary>
internal sealed class HttpWebhookTransport(IHttpClientFactory clients, IOptions<WebhookOptions> options) : IWebhookTransport
{
    public async Task<(int? Status, string? Error)> Send(string url, string body, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Value.Timeout);
        try
        {
            using var response = await clients.CreateClient("webhooks").SendAsync(request, timeout.Token);
            return ((int)response.StatusCode, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return (null, ex.GetType().Name);
        }
    }
}

/// <summary>A webhook the stub transport received.</summary>
public sealed record StubWebhookRequest(string Url, string Body, IReadOnlyDictionary<string, string> Headers);

/// <summary>Records webhooks instead of sending them (tests and local runs).</summary>
public sealed class StubWebhookTransport : IWebhookTransport
{
    public ConcurrentQueue<StubWebhookRequest> Received { get; } = new();

    public Task<(int? Status, string? Error)> Send(string url, string body, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        Received.Enqueue(new StubWebhookRequest(url, body, headers));
        return Task.FromResult<(int?, string?)>(new Uri(url).Host.StartsWith("fail.", StringComparison.Ordinal) ? (503, null) : (200, null));
    }
}

/// <summary>Signing keys from configuration (Integration:Secrets:&lt;ref&gt;), standing in for the platform's secret store.</summary>
internal sealed class ConfigurationSecretStore(IConfiguration configuration) : ISecretStore
{
    public string? Resolve(string secretRef) => configuration[$"Integration:Secrets:{secretRef}"] is { Length: > 0 } secret ? secret : null;
}

/// <summary>Attempts the deliveries that are due, per tenant, one transaction each, as a service identity.</summary>
public sealed class WebhookJobs(IServiceScopeFactory scopes, ITenantDirectory tenants, IClock clock, ILogger<WebhookJobs> logger)
{
    public const string ServiceName = "webhook-jobs";

    public async Task<int> Run(CancellationToken cancellationToken)
    {
        var attempted = 0;
        foreach (var tenantId in await tenants.GetActiveTenantIds(cancellationToken))
        {
            IReadOnlyList<Guid> due;
            await using (var scope = Scope(tenantId))
            {
                var found = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Query(new DueDeliveries(clock.UtcNow, 200), cancellationToken);
                if (found.IsFailure)
                {
                    continue;
                }

                due = found.Value;
            }

            foreach (var id in due)
            {
                await using var scope = Scope(tenantId);
                var result = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new AttemptDelivery(id), cancellationToken);
                if (result.IsSuccess)
                {
                    attempted++;
                }
                else
                {
                    logger.LogWarning("Webhook delivery {DeliveryId} not attempted: {Code}", id, result.Error.Code);
                }
            }
        }

        return attempted;
    }

    private AsyncServiceScope Scope(Guid tenantId)
    {
        var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ExecutionScope>().InitialiseForService(tenantId, ServiceName);
        return scope;
    }
}

public sealed class WebhookJobsService(WebhookJobs jobs, IOptions<WebhookOptions> options, ILogger<WebhookJobsService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.JobEnabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(options.Value.JobInterval);
        do
        {
            try
            {
                await jobs.Run(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("Webhook jobs failed: {ExceptionType}", ex.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
