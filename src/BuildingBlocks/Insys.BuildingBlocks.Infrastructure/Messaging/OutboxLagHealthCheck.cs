using Insys.BuildingBlocks.Application.Context;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Insys.BuildingBlocks.Infrastructure.Messaging;

public sealed class OutboxLagOptions
{
    /// <summary>Degraded when the oldest unpublished event is older than this.</summary>
    public TimeSpan Warning { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Unhealthy (not ready) when the oldest unpublished event is older than this.</summary>
    public TimeSpan Failure { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Outbox relay lag: the age of the oldest event still waiting to be published, across every module outbox
/// (Phase 1 review, item 6). Dead-lettered events are not counted; they are reported as dead letters. Reads only
/// counts and a timestamp, through integration.outbox_lag (V0009) on the API role.
/// </summary>
public sealed class OutboxLagHealthCheck(
    [FromKeyedServices(DataSourceKeys.Api)] NpgsqlDataSource dataSource,
    IClock clock,
    IOptions<OutboxOptions> outbox,
    IOptions<OutboxLagOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("SELECT outbox, oldest_pending, pending FROM integration.outbox_lag(@max)");
        command.Parameters.AddWithValue("max", outbox.Value.MaxAttempts);
        var now = clock.UtcNow;
        var lag = TimeSpan.Zero;
        string? slowest = null;
        long pending = 0;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                pending += reader.GetInt64(2);
                if (!reader.IsDBNull(1) && now - reader.GetFieldValue<DateTimeOffset>(1) is var age && age > lag)
                {
                    lag = age;
                    slowest = reader.GetString(0);
                }
            }
        }

        var data = new Dictionary<string, object>
        {
            ["lagSeconds"] = Math.Round(lag.TotalSeconds),
            ["pending"] = pending,
            ["slowestOutbox"] = slowest ?? "none",
        };
        var settings = options.Value;
        return lag > settings.Failure
            ? HealthCheckResult.Unhealthy($"The oldest unpublished event in {slowest} is {lag:g} old (limit {settings.Failure:g}).", data: data)
            : lag > settings.Warning
                ? HealthCheckResult.Degraded($"The oldest unpublished event in {slowest} is {lag:g} old (warning at {settings.Warning:g}).", data: data)
                : HealthCheckResult.Healthy($"{pending} event(s) waiting.", data);
    }
}
