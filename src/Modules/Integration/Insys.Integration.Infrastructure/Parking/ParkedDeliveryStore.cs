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
using Insys.BuildingBlocks.Application;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.Integration.Application.Parking;
using Insys.Integration.Infrastructure.Persistence;
using Insys.Tenancy.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Insys.Integration.Infrastructure.Parking;

/// <summary>
/// The operations view of integration.parked_delivery and the tenant's parking setting. Reads run in their own
/// transaction in the caller's tenant; changes run in the command's transaction, with its audit entry.
/// </summary>
internal sealed class ParkedDeliveryStore(
    IntegrationDbContext db,
    [FromKeyedServices(IntegrationDbContext.SchemaName)] NpgsqlDataSource dataSource,
    ITenantContext tenant,
    ITenantDirectory tenants,
    ITenantProfiles profiles,
    IClock clock,
    IOptions<ParkingOptions> options) : IParkedDeliveryStore
{
    private const string ViewColumns = """
        id, consumer, event_id, event_type, occurred_at, aggregate_type, aggregate_id, status, prerequisite_code, prerequisite_item, parked_at, attempts,
        last_attempt_at, escalation_level, escalated_at, next_escalation_at, resolved_at, discarded_by, discard_reason, row_version
        """;

    public Task<IReadOnlyList<ParkedDeliveryView>> List(ParkedDeliveryFilter filter, Guid? afterId, int limit, CancellationToken cancellationToken) =>
        ParkedRows.InTenant<IReadOnlyList<ParkedDeliveryView>>(dataSource, tenant.RequireTenantId(), async (connection, transaction) =>
        {
            await using var command = ParkedRows.Command($"""
                SELECT {ViewColumns} FROM integration.parked_delivery
                WHERE tenant_id = @tenant_id
                  AND (@status::text IS NULL OR status = @status)
                  AND (@code::text IS NULL OR prerequisite_code = @code)
                  AND (@consumer::text IS NULL OR consumer = @consumer)
                  AND (@aggregate_type::text IS NULL OR aggregate_type = @aggregate_type)
                  AND (@aggregate_id::uuid IS NULL OR aggregate_id = @aggregate_id)
                  AND (@after::uuid IS NULL OR id > @after)
                ORDER BY id
                LIMIT @limit
                """, connection, transaction,
                ("tenant_id", tenant.RequireTenantId()), ("status", filter.Status is { } s ? ParkedRows.Name(s) : null), ("code", filter.Code),
                ("consumer", filter.Consumer), ("aggregate_type", filter.AggregateType), ("aggregate_id", filter.AggregateId), ("after", afterId), ("limit", limit));
            return await Read(command, cancellationToken);
        }, cancellationToken);

    public Task<ParkedDeliveryView?> Get(Guid id, CancellationToken cancellationToken) =>
        ParkedRows.InTenant(dataSource, tenant.RequireTenantId(), async (connection, transaction) =>
        {
            await using var command = ParkedRows.Command($"SELECT {ViewColumns} FROM integration.parked_delivery WHERE tenant_id = @tenant_id AND id = @id",
                connection, transaction, ("tenant_id", tenant.RequireTenantId()), ("id", id));
            return (await Read(command, cancellationToken)).SingleOrDefault();
        }, cancellationToken);

    public async Task<IReadOnlyList<ParkedDeliveryCount>> ByTenant(CancellationToken cancellationToken)
    {
        var result = new List<ParkedDeliveryCount>();
        foreach (var tenantId in await tenants.GetActiveTenantIds(cancellationToken))
        {
            var kinds = await Counts(dataSource, tenantId, cancellationToken);
            if (kinds.Count > 0)
            {
                var code = (await profiles.Get(tenantId, cancellationToken))?.TenantCode;
                result.AddRange(kinds.Select(k => k with { TenantCode = code }));
            }
        }

        return result;
    }

    public Task<DashboardSummary> Summary(CancellationToken cancellationToken) =>
        ParkedRows.InTenant(dataSource, tenant.RequireTenantId(), async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("""
                SELECT 'P', prerequisite_code, count(*), min(parked_at) FROM integration.parked_delivery WHERE status = 'PARKED' GROUP BY prerequisite_code
                UNION ALL SELECT 'F', NULL, count(*), NULL FROM integration.parked_delivery WHERE status IN ('FAILING', 'RETRYING', 'WAITING')
                UNION ALL SELECT 'D', NULL, count(*), NULL FROM integration.dead_letter_message WHERE resolution IS NULL
                UNION ALL SELECT 'W', NULL, count(*), NULL FROM integration.webhook_delivery WHERE status = 'FAILED' OR (status = 'PENDING' AND http_status IS NOT NULL)
                """, connection, transaction);
            var parked = new List<FigurePart>();
            var (failing, deadLetters, webhooks) = (0L, 0L, 0L);
            DateTimeOffset? oldest = null;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    var count = reader.GetInt64(2);
                    switch (reader.GetString(0))
                    {
                        case "P":
                            parked.Add(new FigurePart(reader.GetString(1), count));
                            var at = reader.GetFieldValue<DateTimeOffset>(3);
                            oldest = oldest is null || at < oldest ? at : oldest;
                            break;
                        case "F": failing = count; break;
                        case "D": deadLetters = count; break;
                        default: webhooks = count; break;
                    }
                }
            }

            return new DashboardSummary(clock.UtcNow,
            [
                new DashboardFigure(DeliveryFigures.Parked, Count: parked.Sum(p => p.Count), By: parked.OrderBy(p => p.Key, StringComparer.Ordinal).ToList()),
                new DashboardFigure(DeliveryFigures.Failing, Count: failing),
                new DashboardFigure(DeliveryFigures.DeadLettersOpen, Count: deadLetters),
                new DashboardFigure(DeliveryFigures.WebhooksFailing, Count: webhooks),
                new DashboardFigure(DeliveryFigures.OldestParkedAt, At: oldest),
            ]);
        }, cancellationToken);

    public async Task<int> RequestRetry(Guid? id, string? code, CancellationToken cancellationToken)
    {
        await using var command = InCommand("""
            UPDATE integration.parked_delivery SET retry_requested_at = @now
            WHERE tenant_id = @tenant_id AND status = 'PARKED' AND (@id::uuid IS NULL OR id = @id) AND (@code::text IS NULL OR prerequisite_code = @code)
            """, ("id", id), ("code", code), ("now", clock.UtcNow));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> Discard(Guid id, long expectedVersion, string by, string reason, CancellationToken cancellationToken)
    {
        await using var command = InCommand("""
            UPDATE integration.parked_delivery
            SET status = 'DISCARDED', discarded_by = @by, discard_reason = @reason, resolved_at = @now,
                row_version = row_version + 1, updated_at = @now, updated_by = @by
            WHERE tenant_id = @tenant_id AND id = @id AND row_version = @version AND status IN ('PARKED', 'WAITING', 'FAILING', 'DEAD_LETTERED')
            """, ("id", id), ("version", expectedVersion), ("by", Truncate(by, 80)), ("reason", reason), ("now", clock.UtcNow));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public Task<ParkingSettingView> Setting(CancellationToken cancellationToken) =>
        ReadSetting(dataSource, tenant.RequireTenantId(), options.Value, cancellationToken);

    public async Task<long> SaveSetting(ParkingSettingView setting, long? expectedVersion, string by, CancellationToken cancellationToken)
    {
        await using var command = InCommand(expectedVersion is null
            ? """
              INSERT INTO integration.parking_setting
                (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, configuration_owner_role, calendar_code, money_first, money_second,
                 other_first, other_second)
              VALUES (@tenant_id, @id, 1, @now, @by, @now, @by, @role, @calendar, @money_first, @money_second, @other_first, @other_second)
              RETURNING row_version
              """
            : """
              UPDATE integration.parking_setting
              SET configuration_owner_role = @role, calendar_code = @calendar, money_first = @money_first, money_second = @money_second,
                  other_first = @other_first, other_second = @other_second, row_version = row_version + 1, updated_at = @now, updated_by = @by
              WHERE tenant_id = @tenant_id AND row_version = @version
              RETURNING row_version
              """,
            ("id", Guid.CreateVersion7()), ("version", expectedVersion), ("now", clock.UtcNow), ("by", Truncate(by, 80)), ("role", setting.ConfigurationOwnerRole),
            ("calendar", setting.CalendarCode), ("money_first", setting.MoneyFirst), ("money_second", setting.MoneySecond), ("other_first", setting.OtherFirst),
            ("other_second", setting.OtherSecond));
        return await command.ExecuteScalarAsync(cancellationToken) as long? ?? throw new DbUpdateConcurrencyException("The parking setting has changed.");
    }

    internal static Task<ParkingSettingView> ReadSetting(NpgsqlDataSource dataSource, Guid tenantId, ParkingOptions defaults, CancellationToken cancellationToken) =>
        ParkedRows.InTenant(dataSource, tenantId, async (connection, transaction) =>
        {
            await using var command = ParkedRows.Command("""
                SELECT configuration_owner_role, calendar_code, money_first, money_second, other_first, other_second, row_version
                FROM integration.parking_setting WHERE tenant_id = @tenant_id
                """, connection, transaction, ("tenant_id", tenantId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken)
                ? new ParkingSettingView(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), reader.GetString(5), false, reader.GetInt64(6))
                : new ParkingSettingView(defaults.DefaultConfigurationOwnerRole, null, defaults.MoneyFirst, defaults.MoneySecond, defaults.OtherFirst,
                    defaults.OtherSecond, true, 0);
        }, cancellationToken);

    /// <summary>A tenant's parked deliveries, by kind.</summary>
    internal static Task<List<ParkedDeliveryCount>> Counts(NpgsqlDataSource dataSource, Guid tenantId, CancellationToken cancellationToken) =>
        ParkedRows.InTenant(dataSource, tenantId, async (connection, transaction) =>
        {
            await using var command = ParkedRows.Command("""
                SELECT consumer, prerequisite_code, prerequisite_item, count(*)::int, max(escalation_level), min(parked_at)
                FROM integration.parked_delivery
                WHERE tenant_id = @tenant_id AND status = 'PARKED'
                GROUP BY consumer, prerequisite_code, prerequisite_item
                ORDER BY prerequisite_code, consumer
                """, connection, transaction, ("tenant_id", tenantId));
            var counts = new List<ParkedDeliveryCount>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                counts.Add(new ParkedDeliveryCount(tenantId, null, reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetInt32(3), reader.GetInt16(4), reader.GetFieldValue<DateTimeOffset>(5)));
            }

            return counts;
        }, cancellationToken);

    private NpgsqlCommand InCommand(string sql, params (string Name, object? Value)[] parameters)
    {
        var transaction = db.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException("Parked delivery changes run inside the command's transaction.");
        return ParkedRows.Command(sql, (NpgsqlConnection)db.Database.GetDbConnection(), (NpgsqlTransaction)transaction,
            [.. parameters, ("tenant_id", tenant.RequireTenantId())]);
    }

    private static async Task<IReadOnlyList<ParkedDeliveryView>> Read(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var views = new List<ParkedDeliveryView>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            DateTimeOffset? At(int i) => reader.IsDBNull(i) ? null : reader.GetFieldValue<DateTimeOffset>(i);
            string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
            views.Add(new ParkedDeliveryView(reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2), reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4),
                reader.GetString(5), reader.GetGuid(6), ParkedRows.Status(reader.GetString(7)), Text(8), Text(9), At(10), reader.GetInt32(11), At(12),
                reader.GetInt16(13), At(14), At(15), At(16), Text(17), Text(18), reader.GetInt64(19)));
        }

        return views;
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}

internal sealed class ConfigurationOwner([FromKeyedServices(IntegrationDbContext.SchemaName)] NpgsqlDataSource dataSource, IOptions<ParkingOptions> options)
    : Contracts.IConfigurationOwner
{
    public async Task<string> Role(Guid tenantId, CancellationToken cancellationToken) =>
        (await ParkedDeliveryStore.ReadSetting(dataSource, tenantId, options.Value, cancellationToken)).ConfigurationOwnerRole;
}
