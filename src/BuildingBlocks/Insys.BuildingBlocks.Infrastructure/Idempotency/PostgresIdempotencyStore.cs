/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Infrastructure
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
using Insys.BuildingBlocks.Application;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Insys.BuildingBlocks.Infrastructure.Idempotency;

public sealed class IdempotencyOptions
{
    /// <summary>TODO(spec): how long a key is kept is not specified (INT-003, DBA-008).</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromHours(24);
}

/// <summary>
/// integration.idempotency_key, owned by the API building block and used through its own role (insys_api), scoped
/// to the tenant by row-level security. A key is reserved IN_PROGRESS before
/// the operation runs and COMPLETED with the response afterwards. The unique (tenant_id, idem_key) constraint
/// settles races between concurrent requests.
/// </summary>
public sealed class PostgresIdempotencyStore(
    [FromKeyedServices(DataSourceKeys.Api)] NpgsqlDataSource dataSource,
    ITenantContext tenant,
    IExecutionContext execution,
    IClock clock,
    IOptions<IdempotencyOptions> options) : IIdempotencyStore
{
    private const string Completed = "COMPLETED";

    public async Task<IdempotencyBeginResult> Begin(string key, string operation, string requestHash, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var now = clock.UtcNow;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);

        await using (var select = new NpgsqlCommand("""
            SELECT request_hash, status, response_ref::text, expires_at
            FROM integration.idempotency_key
            WHERE tenant_id = @tenant_id AND idem_key = @key
            FOR UPDATE
            """, connection, transaction))
        {
            select.Parameters.AddWithValue("tenant_id", tenantId);
            select.Parameters.AddWithValue("key", key);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var storedHash = reader.GetString(0);
                var status = reader.GetString(1);
                var response = reader.IsDBNull(2) ? null : reader.GetString(2);
                var expiresAt = reader.GetFieldValue<DateTimeOffset>(3);
                await reader.CloseAsync();

                if (expiresAt > now)
                {
                    await transaction.CommitAsync(cancellationToken);
                    if (!string.Equals(storedHash, requestHash, StringComparison.Ordinal))
                    {
                        return new(IdempotencyBeginOutcome.KeyReusedWithDifferentRequest);
                    }

                    return status == Completed && response is not null
                        ? new(IdempotencyBeginOutcome.Replay, JsonSerializer.Deserialize<IdempotentResponse>(response, ModuleDbContext.EventJson))
                        : new(IdempotencyBeginOutcome.InProgress);
                }

                await using var expire = new NpgsqlCommand(
                    "DELETE FROM integration.idempotency_key WHERE tenant_id = @tenant_id AND idem_key = @key", connection, transaction);
                expire.Parameters.AddWithValue("tenant_id", tenantId);
                expire.Parameters.AddWithValue("key", key);
                await expire.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await using (var insert = new NpgsqlCommand("""
            INSERT INTO integration.idempotency_key
              (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, idem_key, operation, request_hash, status, response_ref, expires_at)
            VALUES
              (@tenant_id, @id, 1, @now, @actor, @now, @actor, @key, @operation, @hash, 'IN_PROGRESS', NULL, @expires_at)
            ON CONFLICT (tenant_id, idem_key) DO NOTHING
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("tenant_id", tenantId);
            insert.Parameters.AddWithValue("id", Guid.CreateVersion7());
            insert.Parameters.AddWithValue("now", now);
            insert.Parameters.AddWithValue("actor", Truncate(execution.ActorId, 80));
            insert.Parameters.AddWithValue("key", key);
            insert.Parameters.AddWithValue("operation", Truncate(operation, 60));
            insert.Parameters.AddWithValue("hash", requestHash);
            insert.Parameters.AddWithValue("expires_at", now + options.Value.Retention);
            var inserted = await insert.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return inserted == 1 ? new(IdempotencyBeginOutcome.Started) : new(IdempotencyBeginOutcome.InProgress);
        }
    }

    public async Task Complete(string key, IdempotentResponse response, CancellationToken cancellationToken)
    {
        await Execute("""
            UPDATE integration.idempotency_key
            SET status = 'COMPLETED', response_ref = @response, row_version = row_version + 1, updated_at = @now, updated_by = @actor
            WHERE tenant_id = @tenant_id AND idem_key = @key AND status = 'IN_PROGRESS'
            """, key, command => command.Parameters.Add(new NpgsqlParameter("response", NpgsqlDbType.Jsonb)
            {
                Value = JsonSerializer.Serialize(response, ModuleDbContext.EventJson),
            }), cancellationToken);
    }

    public Task Release(string key, CancellationToken cancellationToken) =>
        Execute("DELETE FROM integration.idempotency_key WHERE tenant_id = @tenant_id AND idem_key = @key AND status = 'IN_PROGRESS'",
            key, _ => { }, cancellationToken);

    private async Task Execute(string sql, string key, Action<NpgsqlCommand> addParameters, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("now", clock.UtcNow);
        command.Parameters.AddWithValue("actor", Truncate(execution.ActorId, 80));
        addParameters(command);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}
