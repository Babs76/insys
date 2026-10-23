/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Identity
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
using Insys.Identity.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Insys.Identity.Infrastructure;

/// <summary>
/// The permission cache is per instance (ADR-006): a revocation is immediate on the instance that made it and takes up
/// to the cache lifetime elsewhere. Until evictions are distributed, this guard refuses to start a second API
/// instance on the same database. It holds a PostgreSQL advisory lock on a dedicated connection for the instance's
/// lifetime; the lock is released when the instance stops or its connection drops.
/// </summary>
public sealed class SingleInstanceGuard(
    [FromKeyedServices(IdentityDbContext.SchemaName)] NpgsqlDataSource dataSource,
    IOptions<PermissionCacheOptions> options,
    ILogger<SingleInstanceGuard> logger) : IHostedService, IAsyncDisposable
{
    /// <summary>"INSYSPCI" (INSYS permission cache instance).</summary>
    public const long LockKey = 0x494E_5359_5350_4349;

    private NpgsqlConnection? _connection;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.Value.AllowMultipleInstances)
        {
            logger.LogWarning("Several API instances are allowed, but permission cache eviction is per instance: revocation reaches other instances "
                + "only within {Duration}.", options.Value.Duration);
            return;
        }

        _connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", _connection);
        command.Parameters.AddWithValue("key", LockKey);
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
        {
            await _connection.DisposeAsync();
            _connection = null;
            throw new InvalidOperationException(
                "Another INSYS API instance is already running against this database. The permission cache is per instance (ADR-006), "
                + "so only one instance may run until distributed cache invalidation exists. Set Identity:PermissionCache:AllowMultipleInstances "
                + "only if a revocation delay of up to the cache duration on other instances is acceptable.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken) => await DisposeAsync();

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
    }
}
