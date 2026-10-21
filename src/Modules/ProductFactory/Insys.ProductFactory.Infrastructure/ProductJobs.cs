using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.ProductFactory.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Insys.ProductFactory.Infrastructure;

public sealed class ProductJobOptions
{
    public bool Enabled { get; set; } = true;

    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>Moves published product versions to Effective on their effective date ("Effective date reached"), one version per transaction.</summary>
public sealed class ProductJobs(IServiceScopeFactory scopes, ITenantDirectory tenants, IClock clock, ILogger<ProductJobs> logger)
{
    public const string ServiceName = "product-jobs";

    public async Task<int> Run(CancellationToken cancellationToken)
    {
        var effective = 0;
        foreach (var tenantId in await tenants.GetActiveTenantIds(cancellationToken))
        {
            IReadOnlyList<Guid> due;
            await using (var scope = scopes.CreateAsyncScope())
            {
                scope.ServiceProvider.GetRequiredService<ExecutionScope>().InitialiseForService(tenantId, ServiceName);
                due = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Query(new VersionsDueToBecomeEffective(clock.UtcToday), cancellationToken)
                    is { IsSuccess: true } r ? r.Value : [];
            }

            foreach (var versionId in due)
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<ExecutionScope>().InitialiseForService(tenantId, ServiceName);
                var result = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new MakeVersionEffective(versionId), cancellationToken);
                if (result.IsSuccess)
                {
                    effective++;
                }
                else
                {
                    logger.LogWarning("Could not make product version {VersionId} effective: {Code}", versionId, result.Error.Code);
                }
            }
        }

        return effective;
    }
}

public sealed class ProductJobsService(ProductJobs jobs, IOptions<ProductJobOptions> options, ILogger<ProductJobsService> logger) : BackgroundService
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
                logger.LogError("Product jobs failed: {ExceptionType}", ex.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
