/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Integration
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
using System.Text.Json;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Contracts.StaticData;
using Insys.BuildingBlocks.Infrastructure.Messaging;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.Integration.Application.Parking;
using Insys.Integration.Contracts;
using Insys.Integration.Infrastructure.Persistence;
using Insys.Tenancy.Contracts;
using Insys.Workflow.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Insys.Integration.Infrastructure.Parking;

/// <summary>What one pass of the parking job did, for tests and logs.</summary>
public sealed record ParkingPass(int Retried, int Resolved, int Released, int Escalated, int DeadLettered);

/// <summary>
/// The parking job (ADR-022 decisions 3, 4 and 6), tenant by tenant: retries parked deliveries that are due (configuration
/// published, the periodic check, or a manual retry) and failing deliveries the ledger owns; releases waiting
/// deliveries nothing blocks any more; and escalates parked deliveries on the tenant's schedule. A retry takes its row
/// as RETRYING in a committed transaction before delivering, so two instances never run the same delivery, and the
/// consumer's inbox skips one already applied.
/// </summary>
public sealed class ParkingJob(
    [FromKeyedServices(IntegrationDbContext.SchemaName)] NpgsqlDataSource dataSource,
    IServiceProvider services,
    IServiceScopeFactory scopes,
    ITenantDirectory tenants,
    IEventBus bus,
    IDeliveryLedger ledger,
    IClock clock,
    IOptions<ParkingOptions> options,
    IOptions<OutboxOptions> outbox,
    ILogger<ParkingJob> logger)
{
    public const string ServiceName = "integration-parking";

    public async Task<ParkingPass> RunOnce(CancellationToken cancellationToken)
    {
        var total = new ParkingPass(0, 0, 0, 0, 0);
        foreach (var tenantId in await tenants.GetActiveTenantIds(cancellationToken))
        {
            try
            {
                var pass = await RunForTenant(tenantId, cancellationToken);
                total = new ParkingPass(total.Retried + pass.Retried, total.Resolved + pass.Resolved, total.Released + pass.Released,
                    total.Escalated + pass.Escalated, total.DeadLettered + pass.DeadLettered);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("Parking job failed for tenant {TenantId}: {ExceptionType}", tenantId, ex.GetType().Name);
            }
        }

        return total;
    }

    public async Task<ParkingPass> RunForTenant(Guid tenantId, CancellationToken cancellationToken)
    {
        int retried = 0, resolved = 0, released = 0, deadLettered = 0;
        await RecoverStale(tenantId, cancellationToken);

        foreach (var (envelope, consumer) in await ClaimDue(tenantId, cancellationToken))
        {
            retried++;
            var outcome = await DeliverTo(envelope, consumer, cancellationToken);
            if (outcome == DeliveryStatus.Processed)
            {
                resolved++;
            }
            else if (outcome == DeliveryStatus.Failed && await DeadLetterIfExhausted(envelope, consumer, cancellationToken))
            {
                deadLettered++;
            }
        }

        foreach (var (consumer, aggregateType, aggregateId) in await UnblockedRecords(tenantId, cancellationToken))
        {
            if (await ledger.ClaimNextWaiting(tenantId, consumer, aggregateType, aggregateId, cancellationToken) is { } next)
            {
                released++;
                await DeliverTo(next, consumer, cancellationToken);
            }
        }

        var escalated = await Escalate(tenantId, cancellationToken);
        return new ParkingPass(retried, resolved, released, escalated, deadLettered);
    }

    private async Task<DeliveryStatus> DeliverTo(EventEnvelope envelope, string consumer, CancellationToken cancellationToken)
    {
        try
        {
            return (await bus.DeliverTo(envelope, consumer, cancellationToken)).Status;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The consumer no longer subscribes, or the ledger could not record the outcome: retried as a failure.
            logger.LogError("Parked delivery of event {EventId} to {Consumer} could not run: {ExceptionType}", envelope.EventId, consumer, ex.GetType().Name);
            await ledger.Failing(envelope, consumer, ledgerOwned: true, cancellationToken);
            return DeliveryStatus.Failed;
        }
    }

    private Task<int> RecoverStale(Guid tenantId, CancellationToken cancellationToken) =>
        ParkedRows.InTenant(dataSource, tenantId, async (connection, transaction) =>
        {
            await using var command = ParkedRows.Command("""
                UPDATE integration.parked_delivery
                SET status = 'FAILING', ledger_owned = true, next_attempt_at = @now, row_version = row_version + 1, updated_at = @now, updated_by = @actor
                WHERE tenant_id = @tenant_id AND status = 'RETRYING' AND last_attempt_at < @stale
                """, connection, transaction,
                ("tenant_id", tenantId), ("now", clock.UtcNow), ("stale", clock.UtcNow - options.Value.StaleAfter), ("actor", ServiceName));
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    /// <summary>Takes the deliveries due for a retry as RETRYING, oldest first, and commits before any is delivered.</summary>
    private Task<List<(EventEnvelope Envelope, string Consumer)>> ClaimDue(Guid tenantId, CancellationToken cancellationToken) =>
        ParkedRows.InTenant(dataSource, tenantId, async (connection, transaction) =>
        {
            var now = clock.UtcNow;
            await using var command = ParkedRows.Command($"""
                WITH due AS (
                  SELECT id FROM integration.parked_delivery
                  WHERE tenant_id = @tenant_id
                    AND ((status = 'PARKED' AND (retry_requested_at IS NOT NULL OR last_attempt_at <= @periodic))
                      OR (status = 'FAILING' AND ledger_owned AND next_attempt_at <= @now))
                  ORDER BY occurred_at, event_id
                  LIMIT @batch
                  FOR UPDATE SKIP LOCKED)
                UPDATE integration.parked_delivery d
                SET status = 'RETRYING', retry_requested_at = NULL, last_attempt_at = @now, row_version = d.row_version + 1, updated_at = @now, updated_by = @actor
                FROM due
                WHERE d.tenant_id = @tenant_id AND d.id = due.id
                RETURNING d.consumer, {string.Join(", ", ParkedRows.EnvelopeColumns.Split(", ").Select(c => "d." + c))}
                """, connection, transaction,
                ("tenant_id", tenantId), ("now", now), ("periodic", now - options.Value.RetryInterval), ("batch", options.Value.BatchSize), ("actor", ServiceName));
            var claimed = new List<(EventEnvelope, string)>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                claimed.Add((ParkedRows.Envelope(reader, 1), reader.GetString(0)));
            }

            return claimed.OrderBy(c => c.Item1.OccurredAt).ThenBy(c => c.Item1.EventId).ToList();
        }, cancellationToken);

    /// <summary>Records with waiting deliveries that nothing blocks any more: a release missed by a race, or after a discard.</summary>
    private Task<List<(string Consumer, string AggregateType, Guid AggregateId)>> UnblockedRecords(Guid tenantId, CancellationToken cancellationToken) =>
        ParkedRows.InTenant(dataSource, tenantId, async (connection, transaction) =>
        {
            await using var command = ParkedRows.Command($"""
                SELECT DISTINCT w.consumer, w.aggregate_type, w.aggregate_id FROM integration.parked_delivery w
                WHERE w.tenant_id = @tenant_id AND w.status = 'WAITING'
                  AND NOT EXISTS (
                    SELECT 1 FROM integration.parked_delivery b
                    WHERE b.tenant_id = w.tenant_id AND b.consumer = w.consumer AND b.aggregate_type = w.aggregate_type
                      AND b.aggregate_id = w.aggregate_id AND b.status IN {ParkedRows.BlockingStatuses})
                LIMIT @batch
                """, connection, transaction, ("tenant_id", tenantId), ("batch", options.Value.BatchSize));
            var records = new List<(string, string, Guid)>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                records.Add((reader.GetString(0), reader.GetString(1), reader.GetGuid(2)));
            }

            return records;
        }, cancellationToken);

    /// <summary>A delivery the ledger retries is dead-lettered after Outbox:MaxAttempts consecutive failures, as the outbox's are (INT-006).</summary>
    private async Task<bool> DeadLetterIfExhausted(EventEnvelope envelope, string consumer, CancellationToken cancellationToken)
    {
        var failures = await ParkedRows.InTenant(dataSource, envelope.TenantId, async (connection, transaction) =>
        {
            await using var command = ParkedRows.Command($"""
                UPDATE integration.parked_delivery SET status = 'DEAD_LETTERED', row_version = row_version + 1, updated_at = @now, updated_by = @actor
                WHERE tenant_id = @tenant_id AND consumer = @consumer AND event_id = @event_id AND status = 'FAILING' AND failures >= @max
                RETURNING failures
                """, connection, transaction,
                ("tenant_id", envelope.TenantId), ("consumer", consumer), ("event_id", envelope.EventId), ("max", (int)outbox.Value.MaxAttempts),
                ("now", clock.UtcNow), ("actor", ServiceName));
            return await command.ExecuteScalarAsync(cancellationToken) as int?;
        }, cancellationToken);
        if (failures is not { } attempts)
        {
            return false;
        }

        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ExecutionScope>().InitialiseForService(envelope.TenantId, ServiceName, envelope.CorrelationId, envelope.EventId);
        var source = $"ledger:{consumer}";
        await scope.ServiceProvider.GetRequiredService<IDeadLetterSink>().DeadLetter(envelope, source.Length <= 60 ? source : source[..60],
            DeadLetterErrors.AttemptLimitReached, (short)Math.Min(attempts, short.MaxValue), clock.UtcNow, cancellationToken);
        logger.LogError("Delivery of event {EventId} to {Consumer} is dead-lettered after {Attempts} failures", envelope.EventId, consumer, attempts);
        return true;
    }

    /// <summary>
    /// ADR-022 decision 6: computes each parked delivery's next escalation in business time, then escalates each kind
    /// (consumer, code, item) whose deliveries fall due: a Workflow task to the configuration owner role at level 1,
    /// and to the tenant administrator at level 2, with the platform operator alerted through /health and the log.
    /// </summary>
    private async Task<int> Escalate(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ExecutionScope>().InitialiseForService(tenantId, ServiceName);
        var businessTime = scope.ServiceProvider.GetService<IBusinessTime>();
        var setting = await ParkedDeliveryStore.ReadSetting(dataSource, tenantId, options.Value, cancellationToken);

        async Task<DateTimeOffset> Add(DateTimeOffset start, string duration) =>
            businessTime is null
                ? start + (BusinessDuration.TryParse(duration, out var span) ? span : TimeSpan.FromDays(1))
                : await businessTime.Add(tenantId, setting.CalendarCode, start, duration, cancellationToken);

        string Duration(string consumer, int level)
        {
            var money = options.Value.MoneySchemas.Contains(consumer.Split('.')[0], StringComparer.Ordinal);
            return (money, level) switch
            {
                (true, 1) => setting.MoneyFirst,
                (true, _) => setting.MoneySecond,
                (false, 1) => setting.OtherFirst,
                _ => setting.OtherSecond,
            };
        }

        var unscheduled = await ParkedRows.InTenant(dataSource, tenantId, async (connection, transaction) =>
        {
            await using var command = ParkedRows.Command("""
                SELECT id, consumer, parked_at, escalation_level FROM integration.parked_delivery
                WHERE tenant_id = @tenant_id AND status = 'PARKED' AND escalation_level < 2 AND next_escalation_at IS NULL
                LIMIT @batch
                """, connection, transaction, ("tenant_id", tenantId), ("batch", options.Value.BatchSize));
            var rows = new List<(Guid Id, string Consumer, DateTimeOffset ParkedAt, int Level)>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2), reader.GetInt16(3)));
            }

            return rows;
        }, cancellationToken);

        foreach (var row in unscheduled)
        {
            var due = await Add(row.ParkedAt, Duration(row.Consumer, row.Level + 1));
            await ParkedRows.InTenant(dataSource, tenantId, async (connection, transaction) =>
            {
                await using var command = ParkedRows.Command("""
                    UPDATE integration.parked_delivery SET next_escalation_at = @due
                    WHERE tenant_id = @tenant_id AND id = @id AND next_escalation_at IS NULL
                    """, connection, transaction, ("tenant_id", tenantId), ("id", row.Id), ("due", due));
                return await command.ExecuteNonQueryAsync(cancellationToken);
            }, cancellationToken);
        }

        var groups = await ParkedRows.InTenant(dataSource, tenantId, async (connection, transaction) =>
        {
            await using var command = ParkedRows.Command("""
                SELECT consumer, prerequisite_code, prerequisite_item, escalation_level, count(*)::int,
                       (array_agg(id ORDER BY parked_at, id))[1], min(parked_at)
                FROM integration.parked_delivery
                WHERE tenant_id = @tenant_id AND status = 'PARKED' AND escalation_level < 2 AND next_escalation_at <= @now
                GROUP BY consumer, prerequisite_code, prerequisite_item, escalation_level
                """, connection, transaction, ("tenant_id", tenantId), ("now", clock.UtcNow));
            var rows = new List<(string Consumer, string Code, string? Item, int Level, int Count, Guid Oldest, DateTimeOffset OldestParkedAt)>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetInt16(3), reader.GetInt32(4),
                    reader.GetGuid(5), reader.GetFieldValue<DateTimeOffset>(6)));
            }

            return rows;
        }, cancellationToken);

        foreach (var group in groups)
        {
            var now = clock.UtcNow;
            var level = group.Level + 1;
            var dueAt = level < 2 ? await Add(group.OldestParkedAt, Duration(group.Consumer, 2)) : await Add(now, "P1D");
            var targetRole = level < 2 ? setting.ConfigurationOwnerRole : options.Value.TenantAdministratorRole;
            var escalated = new ParkedDeliveryEscalated(group.Oldest, level, targetRole, group.Consumer, group.Code, group.Item, group.Count, dueAt);
            await ParkedRows.InTenant(dataSource, tenantId, async (connection, transaction) =>
            {
                // The level change and the event commit together, through the Integration module's outbox (DBA-006).
                await using (var update = ParkedRows.Command("""
                    UPDATE integration.parked_delivery
                    SET escalation_level = @level, escalated_at = @now, next_escalation_at = NULL, row_version = row_version + 1, updated_at = @now, updated_by = @actor
                    WHERE tenant_id = @tenant_id AND status = 'PARKED' AND consumer = @consumer AND prerequisite_code = @code
                      AND prerequisite_item IS NOT DISTINCT FROM @item AND escalation_level = @previous AND next_escalation_at <= @now
                    """, connection, transaction,
                    ("tenant_id", tenantId), ("level", (short)level), ("previous", (short)group.Level), ("now", now), ("actor", ServiceName),
                    ("consumer", group.Consumer), ("code", group.Code), ("item", group.Item)))
                {
                    await update.ExecuteNonQueryAsync(cancellationToken);
                }

                await ParkedRows.Publish(connection, transaction, tenantId, group.Oldest, escalated, now, ServiceName, Guid.CreateVersion7(), cancellationToken);
                return 1;
            }, cancellationToken);

            var tenantName = await TenantName.Of(services, tenantId, cancellationToken);
            if (level >= 2)
            {
                // The platform operator's alert (OQ-072): the log alarm, and /health Unhealthy while it stays escalated.
                logger.LogError("ParkedDeliveryEscalated: tenant {Tenant} {Code} {Item} ({Consumer}) x{Count}, level {Level}, to {Role} and platform operations",
                    tenantName, group.Code, group.Item, group.Consumer, group.Count, level, targetRole);
            }
            else
            {
                logger.LogWarning("ParkedDeliveryEscalated: tenant {Tenant} {Code} {Item} ({Consumer}) x{Count}, level {Level}, to {Role}",
                    tenantName, group.Code, group.Item, group.Consumer, group.Count, level, targetRole);
            }
        }

        return groups.Count;
    }
}

public sealed class ParkingJobService(IServiceProvider services, IOptions<ParkingOptions> options, ILogger<ParkingJobService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.JobEnabled)
        {
            return;
        }

        var job = services.GetRequiredService<ParkingJob>();
        using var timer = new PeriodicTimer(options.Value.PollInterval);
        do
        {
            try
            {
                await job.RunOnce(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("Parking job pass failed: {ExceptionType}", ex.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

/// <summary>
/// ADR-022 decision 4, trigger 1: configuration published in a tenant retries all its parked deliveries at the parking
/// job's next pass. One still missing its prerequisite parks again, at no cost.
/// </summary>
public sealed class RetryParkedOnConfiguration(IntegrationDbContext db, IClock clock) : IIntegrationEventHandler<StaticDataVersionActivated>
{
    public async Task Handle(StaticDataVersionActivated activated, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var transaction = db.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException("A policy runs inside the inbox transaction.");
        await using var command = ParkedRows.Command("""
            UPDATE integration.parked_delivery SET retry_requested_at = @now
            WHERE tenant_id = @tenant_id AND status = 'PARKED' AND retry_requested_at IS NULL
            """, (NpgsqlConnection)db.Database.GetDbConnection(), (NpgsqlTransaction)transaction, ("tenant_id", envelope.TenantId), ("now", clock.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>
/// /health check parked-deliveries (ADR-022 decision 5): Degraded while any delivery is parked, Unhealthy once any is
/// escalated. Its data lists each tenant code with its codes, items and counts; never a party or payload.
/// </summary>
public sealed class ParkedDeliveriesHealthCheck(
    [FromKeyedServices(IntegrationDbContext.SchemaName)] NpgsqlDataSource dataSource,
    ITenantDirectory tenants,
    IServiceProvider services) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // data: parked and escalated (numbers, for the Cloud Eye probe), and tenants: each tenant code with its kinds,
        // "CODE ITEM xN (consumer)" (escalated ones marked), so the alert names the tenant and the missing configuration.
        var byTenant = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        int parked = 0, escalated = 0;
        foreach (var tenantId in await tenants.GetActiveTenantIds(cancellationToken))
        {
            var kinds = await ParkedDeliveryStore.Counts(dataSource, tenantId, cancellationToken);
            if (kinds.Count == 0)
            {
                continue;
            }

            parked += kinds.Sum(k => k.Count);
            escalated += kinds.Where(k => k.EscalationLevel > 0).Sum(k => k.Count);
            byTenant[await TenantName.Of(services, tenantId, cancellationToken)] = kinds.Select(Describe).ToList();
        }

        if (parked == 0)
        {
            return HealthCheckResult.Healthy("No parked deliveries.", new Dictionary<string, object> { ["parked"] = 0, ["escalated"] = 0 });
        }

        var summary = string.Join("; ", byTenant.SelectMany(t => t.Value.Select(k => $"{t.Key} {k}")).Take(5));
        var data = new Dictionary<string, object> { ["parked"] = parked, ["escalated"] = escalated, ["tenants"] = byTenant };
        return escalated > 0
            ? new HealthCheckResult(HealthStatus.Unhealthy, $"Parked deliveries escalated (ADR-022): {summary}", data: data)
            : new HealthCheckResult(HealthStatus.Degraded, $"Deliveries parked for missing tenant configuration (ADR-022): {summary}", data: data);
    }

    private static string Describe(ParkedDeliveryCount k) =>
        $"{k.Code}{(k.Item is null ? "" : " " + k.Item)} x{k.Count} ({k.Consumer}){(k.EscalationLevel > 0 ? $", escalated level {k.EscalationLevel}" : "")}";
}

/// <summary>A tenant as alerts name it: its code, or its ID when Tenancy cannot say.</summary>
internal static class TenantName
{
    public static async Task<string> Of(IServiceProvider services, Guid tenantId, CancellationToken cancellationToken) =>
        services.GetService<ITenantProfiles>() is { } profiles && await profiles.Get(tenantId, cancellationToken) is { } profile
            ? profile.TenantCode
            : tenantId.ToString();
}
