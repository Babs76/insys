using System.Collections.Concurrent;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Identity.Application.Users;
using Insys.Identity.Domain.Users;
using Insys.Identity.Infrastructure.Persistence;
using Insys.Identity.Infrastructure.StaticData;
using Insys.Tenancy.Contracts;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Npgsql;

namespace Insys.Identity.Infrastructure;

public sealed class PermissionCacheOptions
{
    /// <summary>How long resolved permissions are cached. Every change in this process evicts at once; other processes catch up within this time (ADR-006).</summary>
    public TimeSpan Duration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// False (the default) makes <see cref="SingleInstanceGuard"/> refuse a second API instance on the same database,
    /// because evictions are not yet shared between instances (ADR-006).
    /// </summary>
    public bool AllowMultipleInstances { get; set; }
}

/// <summary>Resolved principals, keyed by tenant and subject. All entries of a tenant are evicted together (TEN-004: caches are tenant-scoped).</summary>
public sealed class PermissionCache(IMemoryCache cache, IOptions<PermissionCacheOptions> options) : IPermissionCache
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _tenantTokens = new();

    public bool TryGet(Guid tenantId, string subject, out ResolvedPrincipal? principal) => cache.TryGetValue(Key(tenantId, subject), out principal);

    public void Set(Guid tenantId, string subject, ResolvedPrincipal? principal)
    {
        var token = _tenantTokens.GetOrAdd(tenantId, _ => new CancellationTokenSource());
        cache.Set(Key(tenantId, subject), principal, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = options.Value.Duration,
            ExpirationTokens = { new CancellationChangeToken(token.Token) },
        });
    }

    public void EvictTenant(Guid tenantId)
    {
        if (_tenantTokens.TryRemove(tenantId, out var token))
        {
            token.Cancel();
            token.Dispose();
        }
    }

    private static string Key(Guid tenantId, string subject) => $"principal:{tenantId:N}:{subject}";
}

/// <summary>
/// Resolves a token's subject to an IAM user and the permissions of its role assignments in effect today (OQ-014).
/// The tenant must be active (TEN-008) and the user Invited or Active; the first request of an invited user
/// is its first sign-in. Runs on the Identity module's own connection, inside the request's tenant.
/// </summary>
public sealed class PrincipalResolver(
    [FromKeyedServices(IdentityDbContext.SchemaName)] NpgsqlDataSource dataSource,
    PermissionCache cache,
    ITenantStatusQuery tenants,
    IClock clock) : IPrincipalResolver
{
    private static readonly string[] RoleStatuses = IdentityStaticData.Descriptors.Single(d => d.EntityType == typeof(RolesAndPermissionSets)).Lifecycle.InForce;

    public async Task<ResolvedPrincipal?> Resolve(Guid tenantId, string subject, CancellationToken cancellationToken)
    {
        if (cache.TryGet(tenantId, subject, out var cached))
        {
            return cached;
        }

        var principal = await tenants.IsActive(tenantId, cancellationToken) ? await Load(tenantId, subject, cancellationToken) : null;
        cache.Set(tenantId, subject, principal);
        return principal;
    }

    private async Task<ResolvedPrincipal?> Load(Guid tenantId, string subject, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);

        Guid userId;
        string status;
        await using (var find = new NpgsqlCommand("SELECT id, status FROM iam.user_account WHERE tenant_id = @t AND external_subject = @sub LIMIT 2", connection, transaction))
        {
            find.Parameters.AddWithValue("t", tenantId);
            find.Parameters.AddWithValue("sub", subject);
            await using var reader = await find.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            (userId, status) = (reader.GetGuid(0), reader.GetString(1));

            // TODO(spec): one subject in two identity providers is ambiguous until the issuer-to-idp_code mapping exists.
            if (await reader.ReadAsync(cancellationToken))
            {
                return null;
            }
        }

        var invited = UpperCaseEnumConverter<UserStatus>.ToCode(UserStatus.Invited);
        if (status != invited && status != UpperCaseEnumConverter<UserStatus>.ToCode(UserStatus.Active))
        {
            return null;
        }

        var now = clock.UtcNow;
        await using (var signIn = new NpgsqlCommand("""
            UPDATE iam.user_account
            SET status = CASE WHEN status = @invited THEN @active ELSE status END, last_login_at = @now,
                row_version = row_version + 1, updated_at = @now, updated_by = 'sign-in'
            WHERE tenant_id = @t AND id = @id AND (status = @invited OR last_login_at IS NULL OR last_login_at < @now - interval '15 minutes')
            """, connection, transaction))
        {
            signIn.Parameters.AddWithValue("invited", invited);
            signIn.Parameters.AddWithValue("active", UpperCaseEnumConverter<UserStatus>.ToCode(UserStatus.Active));
            signIn.Parameters.AddWithValue("now", now);
            signIn.Parameters.AddWithValue("t", tenantId);
            signIn.Parameters.AddWithValue("id", userId);
            await signIn.ExecuteNonQueryAsync(cancellationToken);
        }

        var permissions = new HashSet<string>(StringComparer.Ordinal);
        await using (var load = new NpgsqlCommand("""
            SELECT DISTINCT p.permission
            FROM iam.user_role_assignment a
            JOIN iam.cfg_roles_and_permission_sets r
              ON r.role_code = a.role_code AND r.tenant_id = a.tenant_id
             AND r.record_status = ANY(@statuses) AND r.effective_from <= @today AND (r.effective_to IS NULL OR r.effective_to > @today)
            CROSS JOIN LATERAL unnest(r.permissions) AS p(permission)
            WHERE a.tenant_id = @t AND a.user_id = @id AND a.status = 'ACTIVE' AND a.valid_from <= @today AND (a.valid_to IS NULL OR a.valid_to > @today)
            """, connection, transaction))
        {
            load.Parameters.AddWithValue("statuses", RoleStatuses);
            load.Parameters.AddWithValue("today", clock.UtcToday);
            load.Parameters.AddWithValue("t", tenantId);
            load.Parameters.AddWithValue("id", userId);
            await using var reader = await load.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                permissions.Add(reader.GetString(0));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new ResolvedPrincipal(userId, permissions);
    }
}

/// <summary>Drops cached permissions when a role or user changes anywhere, or the tenant is suspended (iam consumes TenantSuspended).</summary>
internal sealed class PermissionCacheInvalidation(IPermissionCache cache) :
    IIntegrationEventHandler<Contracts.RoleAssigned>,
    IIntegrationEventHandler<Contracts.RoleRevoked>,
    IIntegrationEventHandler<Contracts.UserDisabled>,
    IIntegrationEventHandler<TenantSuspended>
{
    public Task Handle(Contracts.RoleAssigned integrationEvent, EventEnvelope envelope, CancellationToken cancellationToken) => Evict(envelope);

    public Task Handle(Contracts.RoleRevoked integrationEvent, EventEnvelope envelope, CancellationToken cancellationToken) => Evict(envelope);

    public Task Handle(Contracts.UserDisabled integrationEvent, EventEnvelope envelope, CancellationToken cancellationToken) => Evict(envelope);

    public Task Handle(TenantSuspended integrationEvent, EventEnvelope envelope, CancellationToken cancellationToken) => Evict(envelope);

    private Task Evict(EventEnvelope envelope)
    {
        cache.EvictTenant(envelope.TenantId);
        return Task.CompletedTask;
    }
}
