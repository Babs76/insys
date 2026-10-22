using Insys.Billing.Application;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Insys.Billing.Infrastructure;

public sealed class BillingJobOptions
{
    public bool Enabled { get; set; } = true;

    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>Issues invoices for instalments due and ages accounts for arrears, one account per transaction, as a service identity.</summary>
public sealed class BillingJobs(IServiceScopeFactory scopes, ITenantDirectory tenants, IClock clock, ILogger<BillingJobs> logger)
{
    public const string ServiceName = "billing-jobs";

    public async Task<(int Invoiced, int Aged)> Run(CancellationToken cancellationToken)
    {
        var (invoiced, aged) = (0, 0);
        var today = clock.UtcToday;
        foreach (var tenantId in await tenants.GetActiveTenantIds(cancellationToken))
        {
            foreach (var accountId in await Query(tenantId, new AccountsDue(today, ForInvoicing: true), cancellationToken))
            {
                invoiced += await Send(tenantId, new IssueDueInvoices(accountId, today), cancellationToken) ? 1 : 0;
            }

            foreach (var accountId in await Query(tenantId, new AccountsDue(today, ForInvoicing: false), cancellationToken))
            {
                aged += await Send(tenantId, new AgeAccount(accountId, today), cancellationToken) ? 1 : 0;
            }
        }

        return (invoiced, aged);
    }

    private async Task<IReadOnlyList<Guid>> Query(Guid tenantId, AccountsDue query, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ExecutionScope>().InitialiseForService(tenantId, ServiceName);
        return await scope.ServiceProvider.GetRequiredService<IDispatcher>().Query(query, cancellationToken) is { IsSuccess: true } r ? r.Value : [];
    }

    private async Task<bool> Send<T>(Guid tenantId, ICommand<T> command, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ExecutionScope>().InitialiseForService(tenantId, ServiceName);
        var result = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(command, cancellationToken);
        if (result.IsFailure)
        {
            logger.LogWarning("{Command} failed: {Code}", command.GetType().Name, result.Error.Code);
        }

        return result.IsSuccess;
    }
}

public sealed class BillingJobsService(BillingJobs jobs, IOptions<BillingJobOptions> options, ILogger<BillingJobsService> logger) : BackgroundService
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
                logger.LogError("Billing jobs failed: {ExceptionType}", ex.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
