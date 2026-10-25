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
using Insys.BuildingBlocks.Infrastructure.Messaging;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.Integration.Contracts;
using Insys.Integration.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Insys.Integration.Infrastructure.Parking;

public sealed class ParkingOptions
{
    public bool JobEnabled { get; set; } = true;

    /// <summary>How often the parking job retries, releases and escalates.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>ADR-022 decision 4, trigger 2: a parked delivery is retried at least this often.</summary>
    public TimeSpan RetryInterval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>A delivery left retrying this long (its instance stopped) is retried again.</summary>
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromMinutes(5);

    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// Schemas whose consumers move money (ADR-022 decision 6): Billing, Payments, Finance, claim payments (Claims
    /// Framework), commission (Distribution) and Reinsurance. Their deliveries escalate on the money schedule.
    /// </summary>
    public string[] MoneySchemas { get; set; } = ["billing", "payment", "finance", "claims", "commission", "reinsurance"];

    public string DefaultConfigurationOwnerRole { get; set; } = "TENANT_ADMIN";

    /// <summary>The role of the second escalation's task (OQ-072: the tenant administrator).</summary>
    public string TenantAdministratorRole { get; set; } = "TENANT_ADMIN";

    public string MoneyFirst { get; set; } = "PT4H";

    public string MoneySecond { get; set; } = "P1D";

    public string OtherFirst { get; set; } = "P1D";

    public string OtherSecond { get; set; } = "P3D";
}

/// <summary>SQL shared by the ledger, the job and the store over integration.parked_delivery (V0031).</summary>
internal static class ParkedRows
{
    public const string EnvelopeColumns =
        "event_id, tenant_id, event_type, event_version, occurred_at, aggregate_type, aggregate_id, correlation_id, causation_id, payload::text";

    public const string OpenStatuses = "('PARKED', 'WAITING', 'FAILING', 'RETRYING', 'DEAD_LETTERED')";

    public const string BlockingStatuses = "('PARKED', 'FAILING', 'RETRYING', 'DEAD_LETTERED')";

    public static string Name(LedgerStatus status) => status switch
    {
        LedgerStatus.DeadLettered => "DEAD_LETTERED",
        _ => status.ToString().ToUpperInvariant(),
    };

    public static LedgerStatus Status(string name) => name switch
    {
        "DEAD_LETTERED" => LedgerStatus.DeadLettered,
        _ => Enum.Parse<LedgerStatus>(name, ignoreCase: true),
    };

    /// <summary>Reads the envelope columns starting at <paramref name="offset"/>.</summary>
    public static EventEnvelope Envelope(NpgsqlDataReader reader, int offset = 0)
    {
        using var payload = JsonDocument.Parse(reader.GetString(offset + 9));
        return new EventEnvelope(reader.GetGuid(offset), reader.GetGuid(offset + 1), reader.GetString(offset + 2), reader.GetInt16(offset + 3),
            reader.GetFieldValue<DateTimeOffset>(offset + 4), reader.GetString(offset + 5), reader.GetGuid(offset + 6), reader.GetGuid(offset + 7),
            reader.IsDBNull(offset + 8) ? null : reader.GetGuid(offset + 8), payload.RootElement.Clone());
    }

    public static async Task<T> InTenant<T>(NpgsqlDataSource dataSource, Guid tenantId, Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> work,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
        var result = await work(connection, transaction);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    /// <summary>Writes an Integration event to the module's outbox in the caller's transaction (DBA-006).</summary>
    public static async Task Publish<TEvent>(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid tenantId, Guid aggregateId, TEvent integrationEvent,
        DateTimeOffset now, string actor, Guid correlationId, CancellationToken cancellationToken)
        where TEvent : IIntegrationEvent
    {
        var type = IntegrationEventTypes.Describe(typeof(TEvent));
        await using var insert = Command("""
            INSERT INTO integration.outbox_event
              (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, aggregate_type, aggregate_id, event_type, event_version,
               payload, occurred_at, correlation_id, causation_id, published_at, publish_attempts)
            VALUES (@tenant_id, @id, 1, @now, @actor, @now, @actor, 'ParkedDelivery', @aggregate_id, @event_type, @event_version,
               @payload::jsonb, @now, @correlation_id, NULL, NULL, 0)
            """, connection, transaction,
            ("tenant_id", tenantId), ("id", Guid.CreateVersion7()), ("now", now), ("actor", actor), ("aggregate_id", aggregateId),
            ("event_type", type.Type), ("event_version", type.Version), ("payload", JsonSerializer.Serialize(integrationEvent, ModuleDbContext.EventJson)),
            ("correlation_id", correlationId));
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    public static NpgsqlCommand Command(string sql, NpgsqlConnection connection, NpgsqlTransaction transaction, params (string Name, object? Value)[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }
}

/// <summary>
/// The delivery ledger (ADR-022) in integration.parked_delivery, on the Integration module's own role. Each call is its
/// own short transaction in the event's tenant, so the record-key check and the outcome are written whatever the
/// consumer's transaction did. A parked delivery's own changes were rolled back with the consumer's transaction.
/// </summary>
public sealed class PostgresDeliveryLedger(
    [FromKeyedServices(IntegrationDbContext.SchemaName)] NpgsqlDataSource dataSource,
    IServiceProvider services,
    IClock clock,
    IOptions<OutboxOptions> outbox,
    ILogger<PostgresDeliveryLedger> logger) : IDeliveryLedger
{
    private const string Actor = "delivery-ledger";

    private const string InsertColumns = """
        (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, consumer, event_id, event_type, event_version, occurred_at,
         aggregate_type, aggregate_id, correlation_id, causation_id, payload, status, ledger_owned, prerequisite_code, prerequisite_item, parked_at,
         attempts, failures, last_attempt_at, next_attempt_at, escalation_level)
        """;

    private const string InsertValues = """
        (@tenant_id, @id, 1, @now, @actor, @now, @actor, @consumer, @event_id, @event_type, @event_version, @occurred_at,
         @aggregate_type, @aggregate_id, @correlation_id, @causation_id, @payload::jsonb, @status, @ledger_owned, @code, @item, @parked_at,
         @attempts, @failures, @last_attempt_at, @next_attempt_at, 0)
        """;

    private const string Touch = "row_version = parked_delivery.row_version + 1, updated_at = @now, updated_by = @actor";

    public bool Enabled => true;

    public Task<IReadOnlyList<LedgerEntry>> ForRecord(EventEnvelope envelope, CancellationToken cancellationToken) =>
        ParkedRows.InTenant<IReadOnlyList<LedgerEntry>>(dataSource, envelope.TenantId, async (connection, transaction) =>
        {
            await using var command = ParkedRows.Command($"""
                SELECT consumer, event_id, status FROM integration.parked_delivery
                WHERE tenant_id = @tenant_id
                  AND ((aggregate_type = @aggregate_type AND aggregate_id = @aggregate_id AND status IN {ParkedRows.OpenStatuses}) OR event_id = @event_id)
                """, connection, transaction,
                ("tenant_id", envelope.TenantId), ("aggregate_type", envelope.AggregateType), ("aggregate_id", envelope.AggregateId), ("event_id", envelope.EventId));
            var entries = new List<LedgerEntry>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                entries.Add(new LedgerEntry(reader.GetString(0), reader.GetGuid(1), ParkedRows.Status(reader.GetString(2))));
            }

            return entries;
        }, cancellationToken);

    public async Task Park(EventEnvelope envelope, string consumer, string code, string? item, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var firstOfKind = await ParkedRows.InTenant(dataSource, envelope.TenantId, async (connection, transaction) =>
        {
            Guid id;
            bool first, newlyParked;
            await using var command = Upsert(envelope, consumer, connection, transaction, now, "PARKED", ledgerOwned: true, code, item, parkedAt: now, failures: 0, nextAttemptAt: null,
                $"""
                status = 'PARKED', ledger_owned = true, prerequisite_code = EXCLUDED.prerequisite_code, prerequisite_item = EXCLUDED.prerequisite_item,
                parked_at = COALESCE(parked_delivery.parked_at, EXCLUDED.parked_at), attempts = parked_delivery.attempts + 1, failures = 0,
                last_attempt_at = @now, next_attempt_at = NULL, retry_requested_at = NULL, {Touch}
                """,
                """
                RETURNING id, xmax = 0 AND NOT EXISTS (
                  SELECT 1 FROM integration.parked_delivery o
                  WHERE o.tenant_id = @tenant_id AND o.status = 'PARKED' AND o.consumer = @consumer AND o.prerequisite_code = @code
                    AND o.prerequisite_item IS NOT DISTINCT FROM @item AND o.event_id <> @event_id),
                  -- Parked for the first time: no earlier row, or one that had never parked (it was failing).
                  NOT EXISTS (SELECT 1 FROM prior WHERE prior.parked_at IS NOT NULL)
                """,
                """
                WITH prior AS (SELECT parked_at FROM integration.parked_delivery WHERE tenant_id = @tenant_id AND consumer = @consumer AND event_id = @event_id)
                """);
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                await reader.ReadAsync(cancellationToken);
                (id, first, newlyParked) = (reader.GetGuid(0), reader.GetBoolean(1), reader.GetBoolean(2));
            }

            if (newlyParked)
            {
                await ParkedRows.Publish(connection, transaction, envelope.TenantId, id, new DeliveryParked(id, consumer, code, item, envelope.EventType, envelope.EventId),
                    now, Actor, envelope.CorrelationId, cancellationToken);
            }

            return first;
        }, cancellationToken);

        if (firstOfKind)
        {
            // ADR-022 decision 5: once per tenant and kind, for the log alarm. Configuration names only, never a party or payload.
            logger.LogWarning("ParkedDeliveryRaised: tenant {Tenant} {Code} {Item} ({Consumer}), event type {EventType}",
                await TenantName.Of(services, envelope.TenantId, cancellationToken), code, item, consumer, envelope.EventType);
        }
    }

    public Task Wait(EventEnvelope envelope, string consumer, CancellationToken cancellationToken) =>
        ParkedRows.InTenant(dataSource, envelope.TenantId, async (connection, transaction) =>
        {
            await using var command = Upsert(envelope, consumer, connection, transaction, clock.UtcNow, "WAITING", ledgerOwned: true, null, null, null, 0, null, null, null);
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    public Task Failing(EventEnvelope envelope, string consumer, bool ledgerOwned, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var backoff = outbox.Value.BaseBackoff;
        return ParkedRows.InTenant(dataSource, envelope.TenantId, async (connection, transaction) =>
        {
            // A replayed dead letter that fails again is the ledger's to retry: the outbox has already given up on it.
            await using var command = Upsert(envelope, consumer, connection, transaction, now, "FAILING", ledgerOwned, null, null, null, 1, now + backoff,
                $"""
                status = 'FAILING', ledger_owned = parked_delivery.ledger_owned OR EXCLUDED.ledger_owned OR parked_delivery.status = 'DEAD_LETTERED',
                attempts = parked_delivery.attempts + 1, failures = parked_delivery.failures + 1, last_attempt_at = @now,
                next_attempt_at = @now + (@backoff * power(2, parked_delivery.failures)), retry_requested_at = NULL, {Touch}
                """, null);
            command.Parameters.AddWithValue("backoff", backoff);
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);
    }

    /// <summary>Resolves the delivery; one that had parked publishes ParkedDeliveryResolved in the same transaction.</summary>
    public Task Resolve(EventEnvelope envelope, string consumer, CancellationToken cancellationToken) =>
        ParkedRows.InTenant(dataSource, envelope.TenantId, async (connection, transaction) =>
        {
            var now = clock.UtcNow;
            (Guid Id, string Code, string? Item)? parked = null;
            await using (var command = ParkedRows.Command($"""
                UPDATE integration.parked_delivery SET status = 'RESOLVED', resolved_at = @now, {Touch}
                WHERE tenant_id = @tenant_id AND consumer = @consumer AND event_id = @event_id AND status NOT IN ('RESOLVED', 'DISCARDED')
                RETURNING id, prerequisite_code, prerequisite_item, parked_at IS NOT NULL
                """, connection, transaction,
                ("tenant_id", envelope.TenantId), ("consumer", consumer), ("event_id", envelope.EventId), ("now", now), ("actor", Actor)))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                if (await reader.ReadAsync(cancellationToken) && reader.GetBoolean(3) && !reader.IsDBNull(1))
                {
                    parked = (reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
                }
            }

            if (parked is { } p)
            {
                await ParkedRows.Publish(connection, transaction, envelope.TenantId, p.Id,
                    new ParkedDeliveryResolved(p.Id, consumer, p.Code, p.Item, envelope.EventType, envelope.EventId), now, Actor, envelope.CorrelationId, cancellationToken);
            }

            return 0;
        }, cancellationToken);

    public Task DeadLettered(EventEnvelope envelope, IEnumerable<string> consumers, CancellationToken cancellationToken) =>
        Update(envelope.TenantId, $"""
            UPDATE integration.parked_delivery SET status = 'DEAD_LETTERED', {Touch}
            WHERE tenant_id = @tenant_id AND event_id = @event_id AND consumer = ANY(@consumers) AND status = 'FAILING'
            """, cancellationToken, ("event_id", envelope.EventId), ("consumers", consumers.ToArray()));

    public Task<EventEnvelope?> ClaimNextWaiting(Guid tenantId, string consumer, string aggregateType, Guid aggregateId, CancellationToken cancellationToken) =>
        ParkedRows.InTenant(dataSource, tenantId, async (connection, transaction) =>
        {
            // The earliest waiting delivery only, never one behind it: if another instance has it, this one waits its turn.
            await using var command = ParkedRows.Command($"""
                WITH next AS (
                  SELECT w.id FROM integration.parked_delivery w
                  WHERE w.tenant_id = @tenant_id AND w.consumer = @consumer AND w.aggregate_type = @aggregate_type AND w.aggregate_id = @aggregate_id
                    AND w.status = 'WAITING'
                    AND NOT EXISTS (
                      SELECT 1 FROM integration.parked_delivery b
                      WHERE b.tenant_id = w.tenant_id AND b.consumer = w.consumer AND b.aggregate_type = w.aggregate_type
                        AND b.aggregate_id = w.aggregate_id AND b.status IN {ParkedRows.BlockingStatuses})
                  ORDER BY w.occurred_at, w.event_id
                  LIMIT 1)
                UPDATE integration.parked_delivery d
                SET status = 'RETRYING', attempts = d.attempts + 1, last_attempt_at = @now, row_version = d.row_version + 1, updated_at = @now, updated_by = @actor
                FROM next
                WHERE d.tenant_id = @tenant_id AND d.id = next.id AND d.status = 'WAITING'
                RETURNING {string.Join(", ", ParkedRows.EnvelopeColumns.Split(", ").Select(c => "d." + c))}
                """, connection, transaction,
                ("tenant_id", tenantId), ("consumer", consumer), ("aggregate_type", aggregateType), ("aggregate_id", aggregateId), ("now", clock.UtcNow), ("actor", Actor));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ParkedRows.Envelope(reader) : null;
        }, cancellationToken);

    private Task Update(Guid tenantId, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters) =>
        ParkedRows.InTenant(dataSource, tenantId, async (connection, transaction) =>
        {
            await using var command = ParkedRows.Command(sql, connection, transaction, [.. parameters, ("tenant_id", tenantId), ("now", clock.UtcNow), ("actor", Actor)]);
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    private static NpgsqlCommand Upsert(EventEnvelope envelope, string consumer, NpgsqlConnection connection, NpgsqlTransaction transaction, DateTimeOffset now,
        string status, bool ledgerOwned, string? code, string? item, DateTimeOffset? parkedAt, int failures, DateTimeOffset? nextAttemptAt, string? onConflict,
        string? returning, string? with = null) =>
        ParkedRows.Command($"""
            {with}
            INSERT INTO integration.parked_delivery {InsertColumns}
            VALUES {InsertValues}
            ON CONFLICT (tenant_id, consumer, event_id) {(onConflict is null ? "DO NOTHING" : "DO UPDATE SET " + onConflict)}
            {returning}
            """, connection, transaction,
            ("tenant_id", envelope.TenantId), ("id", Guid.CreateVersion7()), ("now", now), ("actor", Actor), ("consumer", consumer),
            ("event_id", envelope.EventId), ("event_type", envelope.EventType), ("event_version", envelope.EventVersion), ("occurred_at", envelope.OccurredAt),
            ("aggregate_type", envelope.AggregateType), ("aggregate_id", envelope.AggregateId), ("correlation_id", envelope.CorrelationId),
            ("causation_id", envelope.CausationId), ("payload", envelope.Payload.GetRawText()), ("status", status), ("ledger_owned", ledgerOwned),
            ("code", code), ("item", item), ("parked_at", parkedAt), ("attempts", status == "WAITING" ? 0 : 1), ("failures", failures),
            ("last_attempt_at", status == "WAITING" ? null : now), ("next_attempt_at", nextAttemptAt));
}
