using System.Text.Json;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Insys.BuildingBlocks.Infrastructure.Messaging;

/// <summary>A module schema whose outbox the relay drains.</summary>
public sealed record OutboxSchema(string Name);

public sealed class OutboxOptions
{
    public bool RelayEnabled { get; set; } = true;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    public int BatchSize { get; set; } = 100;

    /// <summary>TODO(spec): INT-006 asks for retries "up to the policy limit"; the limit is not specified.</summary>
    public short MaxAttempts { get; set; } = 10;

    /// <summary>Back-off before attempt n is BaseBackoff × 2^(n−1).</summary>
    public TimeSpan BaseBackoff { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Publishes unpublished outbox rows with retries and back-off, then sets published_at (§23.2). Works one tenant at
/// a time, setting app.tenant_id for each, because the relay's role has no BYPASSRLS (DBA-002). Rows that reach the
/// attempt limit are dead-lettered and left unpublished.
/// </summary>
public sealed class OutboxProcessor(
    NpgsqlDataSource dataSource,
    IEnumerable<OutboxSchema> schemas,
    ITenantDirectory tenants,
    IEventBus bus,
    IServiceScopeFactory scopeFactory,
    IClock clock,
    IOptions<OutboxOptions> options,
    ILogger<OutboxProcessor> logger)
{
    private readonly string[] _schemas = schemas.Select(s => ValidateSchema(s.Name)).Distinct().ToArray();

    /// <summary>Processes one batch per tenant and schema. Returns the number of rows published.</summary>
    public async Task<int> ProcessOnce(CancellationToken cancellationToken)
    {
        var published = 0;
        foreach (var tenantId in await tenants.GetActiveTenantIds(cancellationToken))
        {
            foreach (var schema in _schemas)
            {
                published += await ProcessBatch(schema, tenantId, cancellationToken);
            }
        }

        return published;
    }

    private async Task<int> ProcessBatch(string schema, Guid tenantId, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);

        var rows = new List<OutboxRow>();
        await using (var select = new NpgsqlCommand($"""
            SELECT id, aggregate_type, aggregate_id, event_type, event_version, payload::text, occurred_at, correlation_id, causation_id, publish_attempts, created_at
            FROM {schema}.outbox_event
            WHERE tenant_id = @tenant_id
              AND published_at IS NULL
              AND publish_attempts < @max_attempts
              AND (publish_attempts = 0 OR updated_at <= @now - (@base_backoff * power(2, publish_attempts - 1)))
            ORDER BY occurred_at, id
            LIMIT @batch_size
            FOR UPDATE SKIP LOCKED
            """, connection, transaction))
        {
            select.Parameters.AddWithValue("tenant_id", tenantId);
            select.Parameters.AddWithValue("max_attempts", settings.MaxAttempts);
            select.Parameters.AddWithValue("now", clock.UtcNow);
            select.Parameters.AddWithValue("base_backoff", settings.BaseBackoff);
            select.Parameters.AddWithValue("batch_size", settings.BatchSize);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new OutboxRow(
                    reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2), reader.GetString(3), reader.GetInt16(4), reader.GetString(5),
                    reader.GetFieldValue<DateTimeOffset>(6), reader.GetGuid(7), reader.IsDBNull(8) ? null : reader.GetGuid(8), reader.GetInt16(9),
                    reader.GetFieldValue<DateTimeOffset>(10)));
            }
        }

        var published = 0;
        foreach (var row in rows)
        {
            using var payload = JsonDocument.Parse(row.Payload);
            var envelope = new EventEnvelope(row.Id, tenantId, row.EventType, row.EventVersion, row.OccurredAt, row.AggregateType,
                row.AggregateId, row.CorrelationId, row.CausationId, payload.RootElement.Clone());
            try
            {
                await bus.Publish(envelope, cancellationToken);
                await MarkPublished(connection, transaction, schema, tenantId, row.Id, cancellationToken);
                published++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var attempts = (short)(row.PublishAttempts + 1);
                await MarkFailed(connection, transaction, schema, tenantId, row.Id, attempts, cancellationToken);
                if (attempts >= settings.MaxAttempts)
                {
                    logger.LogError("Outbox event {EventId} in {Schema} is dead-lettered after {Attempts} attempts", row.Id, schema, attempts);

                    // TODO(spec): the outbox has no first-failure column, so the row's creation time stands in for it.
                    await DeadLetter(envelope, schema, ex, attempts, row.CreatedAt, cancellationToken);
                }
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return published;
    }

    private async Task MarkPublished(NpgsqlConnection connection, NpgsqlTransaction transaction, string schema, Guid tenantId, Guid id, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            UPDATE {schema}.outbox_event
            SET published_at = @now, publish_attempts = publish_attempts + 1, row_version = row_version + 1, updated_at = @now, updated_by = 'outbox-relay'
            WHERE tenant_id = @tenant_id AND id = @id
            """, connection, transaction);
        command.Parameters.AddWithValue("now", clock.UtcNow);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task MarkFailed(NpgsqlConnection connection, NpgsqlTransaction transaction, string schema, Guid tenantId, Guid id, short attempts, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            UPDATE {schema}.outbox_event
            SET publish_attempts = @attempts, row_version = row_version + 1, updated_at = @now, updated_by = 'outbox-relay'
            WHERE tenant_id = @tenant_id AND id = @id
            """, connection, transaction);
        command.Parameters.AddWithValue("attempts", attempts);
        command.Parameters.AddWithValue("now", clock.UtcNow);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task DeadLetter(EventEnvelope envelope, string schema, Exception ex, short attempts, DateTimeOffset firstFailedAt, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ExecutionScope>()
            .InitialiseForService(envelope.TenantId, "outbox-relay", envelope.CorrelationId, envelope.EventId);
        var sink = scope.ServiceProvider.GetService<IDeadLetterSink>();
        if (sink is null)
        {
            logger.LogError("No dead-letter sink is registered; event {EventId} stays unpublished", envelope.EventId);
            return;
        }

        // The error text is the exception type only; messages can carry sensitive values.
        await sink.DeadLetter(envelope, $"outbox:{schema}", Describe(ex), attempts, firstFailedAt, cancellationToken);
    }

    private static string Describe(Exception ex) =>
        ex is AggregateException aggregate
            ? string.Join("; ", aggregate.Flatten().InnerExceptions.Select(e => e.GetType().FullName))
            : ex.GetType().FullName ?? ex.GetType().Name;

    private static string ValidateSchema(string name) =>
        name.All(c => char.IsAsciiLetterLower(c) || c == '_') ? name : throw new ArgumentException($"Invalid schema name '{name}'.");

    private sealed record OutboxRow(Guid Id, string AggregateType, Guid AggregateId, string EventType, short EventVersion, string Payload,
        DateTimeOffset OccurredAt, Guid CorrelationId, Guid? CausationId, short PublishAttempts, DateTimeOffset CreatedAt);
}

/// <summary>Runs <see cref="OutboxProcessor"/> on a timer.</summary>
public sealed class OutboxRelayService(OutboxProcessor processor, IOptions<OutboxOptions> options, ILogger<OutboxRelayService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.RelayEnabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(options.Value.PollInterval);
        do
        {
            try
            {
                await processor.ProcessOnce(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("Outbox relay pass failed: {ExceptionType}", ex.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
