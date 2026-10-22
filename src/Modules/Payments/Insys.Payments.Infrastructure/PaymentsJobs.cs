using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.Payments.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Insys.Payments.Infrastructure;

public sealed class PaymentJobOptions
{
    public bool Enabled { get; set; } = true;

    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>A payment pending longer than this without an answer is Unknown (PAY-005). TODO(spec): the time allowed is not specified (OQ-058).</summary>
    public TimeSpan UnknownAfter { get; set; } = TimeSpan.FromHours(24);
}

/// <summary>Marks payments pending too long as Unknown, one per transaction, as a service identity; each then goes to reconciliation (PAY-005).</summary>
public sealed class PaymentJobs(IServiceScopeFactory scopes, ITenantDirectory tenants, IClock clock, IOptions<PaymentJobOptions> options, ILogger<PaymentJobs> logger)
{
    public const string ServiceName = "payment-jobs";

    public async Task<int> Run(CancellationToken cancellationToken)
    {
        var marked = 0;
        foreach (var tenantId in await tenants.GetActiveTenantIds(cancellationToken))
        {
            IReadOnlyList<Guid> pending;
            await using (var scope = Scope(tenantId))
            {
                pending = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Query(new PendingPayments(clock.UtcNow - options.Value.UnknownAfter), cancellationToken)
                    is { IsSuccess: true } r ? r.Value : [];
            }

            foreach (var paymentId in pending)
            {
                await using var scope = Scope(tenantId);
                var result = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new MarkUnknown(paymentId), cancellationToken);
                if (result.IsSuccess)
                {
                    marked++;
                }
                else
                {
                    logger.LogWarning("Could not mark payment {PaymentId} unknown: {Code}", paymentId, result.Error.Code);
                }
            }
        }

        return marked;
    }

    private AsyncServiceScope Scope(Guid tenantId)
    {
        var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ExecutionScope>().InitialiseForService(tenantId, ServiceName);
        return scope;
    }
}

public sealed class PaymentJobsService(PaymentJobs jobs, IOptions<PaymentJobOptions> options, ILogger<PaymentJobsService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(options.Value.Interval);
        do
        {
            try
            {
                await jobs.Run(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("Payment jobs failed: {ExceptionType}", ex.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
