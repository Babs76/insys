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
