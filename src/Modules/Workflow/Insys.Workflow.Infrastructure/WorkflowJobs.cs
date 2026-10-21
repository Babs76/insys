using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.Workflow.Application;
using Insys.Workflow.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Insys.Workflow.Infrastructure;

/// <summary>
/// Expires approvals past their expiry and fires due SLA timers (WFL-003), one tenant and one aggregate at a time,
/// as a service identity. Each aggregate is its own transaction.
/// </summary>
public sealed class WorkflowJobs(IServiceScopeFactory scopes, ITenantDirectory tenants, IClock clock, ILogger<WorkflowJobs> logger)
{
    public const string ServiceName = "workflow-jobs";

    public async Task<(int Expired, int TimersFired)> Run(CancellationToken cancellationToken)
    {
        var (expired, fired) = (0, 0);
        foreach (var tenantId in await tenants.GetActiveTenantIds(cancellationToken))
        {
            var now = clock.UtcNow;
            foreach (var approvalId in await Query(tenantId, new DueApprovals(now), cancellationToken))
            {
                expired += await Send(tenantId, new ExpireApproval(approvalId), cancellationToken) ? 1 : 0;
            }

            foreach (var processId in await Query(tenantId, new ProcessesWithDueTimers(now), cancellationToken))
            {
                fired += await Send(tenantId, new FireTimers(processId), cancellationToken) ? 1 : 0;
            }
        }

        return (expired, fired);
    }

    private async Task<IReadOnlyList<Guid>> Query(Guid tenantId, IQuery<IReadOnlyList<Guid>> query, CancellationToken cancellationToken)
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

public sealed class WorkflowJobsService(WorkflowJobs jobs, IOptions<WorkflowOptions> options, ILogger<WorkflowJobsService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.TimersEnabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(options.Value.TimerInterval);
        do
        {
            try
            {
                await jobs.Run(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("Workflow jobs failed: {ExceptionType}", ex.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
