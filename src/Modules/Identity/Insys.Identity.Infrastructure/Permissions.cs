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
using System.Collections.Concurrent;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
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

    /// <summary>Every tenant's entries: a global change (the catalogue, an entitlement) reaches every user.</summary>
    public void EvictAll()
    {
        foreach (var tenantId in _tenantTokens.Keys)
        {
            EvictTenant(tenantId);
        }
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
    IOrganisationStructure organisation,
    Distribution.Contracts.IIntermediaries intermediaries,
    FunctionCatalogueReader catalogue,
    ITenantFunctionSettings functionSettings,
    IOptions<PlatformOptions> platform,
    IClock clock) : IPrincipalResolver
{
    private static readonly string[] RoleStatuses = IdentityStaticData.Descriptors.Single(d => d.EntityType == typeof(RolesAndPermissionSets)).Lifecycle.InForce;
    private static readonly string[] GrantStatuses = IdentityStaticData.Descriptors.Single(d => d.EntityType == typeof(RoleFunctionAssignments)).Lifecycle.InForce;

    public async Task<ResolvedPrincipal?> Resolve(Guid tenantId, string subject, CancellationToken cancellationToken)
    {
        if (cache.TryGet(tenantId, subject, out var cached))
        {
            return cached;
        }

        var access = await tenants.UserAccess(tenantId, cancellationToken);
        var principal = access == TenantUserAccess.None ? null : await Load(tenantId, subject, access == TenantUserAccess.ConfigurationOnly, cancellationToken);
        cache.Set(tenantId, subject, principal);
        return principal;
    }

    private async Task<ResolvedPrincipal?> Load(Guid tenantId, string subject, bool configurationOnly, CancellationToken cancellationToken)
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

        // ADR-026: each assignment's functions in effect, with its reach (F103): the assignment's own product and branch scopes, else its role's.
        var granted = new List<(string Function, string[]? Products, string[]? Branches)>();
        var roles = new HashSet<string>(StringComparer.Ordinal);
        await using (var load = new NpgsqlCommand("""
            SELECT g.function_code, coalesce(a.scope_products, r.scope_products), coalesce(a.scope_branches, r.scope_branches), a.role_code
            FROM iam.user_role_assignment a
            JOIN iam.cfg_roles_and_permission_sets r
              ON r.role_code = a.role_code AND r.tenant_id = a.tenant_id
             AND r.record_status = ANY(@statuses) AND r.effective_from <= @today AND (r.effective_to IS NULL OR r.effective_to > @today)
            JOIN iam.cfg_role_function_assignments g
              ON g.role_code = a.role_code AND g.tenant_id = a.tenant_id
             AND g.record_status = ANY(@grants) AND g.effective_from <= @today AND (g.effective_to IS NULL OR g.effective_to > @today)
            WHERE a.tenant_id = @t AND a.user_id = @id AND a.status = 'ACTIVE' AND a.valid_from <= @today AND (a.valid_to IS NULL OR a.valid_to > @today)
            """, connection, transaction))
        {
            load.Parameters.AddWithValue("statuses", RoleStatuses);
            load.Parameters.AddWithValue("grants", GrantStatuses);
            load.Parameters.AddWithValue("today", clock.UtcToday);
            load.Parameters.AddWithValue("t", tenantId);
            load.Parameters.AddWithValue("id", userId);
            await using var reader = await load.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                granted.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetFieldValue<string[]>(1), reader.IsDBNull(2) ? null : reader.GetFieldValue<string[]>(2)));
                roles.Add(reader.GetString(3));
            }
        }

        // An external user's approved link (external-user-access.md §1); a broker's codes include its sub-brokers' (§2).
        (string Kind, Guid? Party, string? Intermediary, string? Provider)? link = null;
        await using (var linked = new NpgsqlCommand("""
            SELECT kind, party_id, intermediary_code, provider_id FROM iam.user_subject_link WHERE tenant_id = @t AND user_id = @id AND status = 'ACTIVE'
            """, connection, transaction))
        {
            linked.Parameters.AddWithValue("t", tenantId);
            linked.Parameters.AddWithValue("id", userId);
            await using var reader = await linked.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                link = (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        var external = link is { } l
            ? new ExternalSubject(l.Kind, l.Party, l.Intermediary,
                l.Intermediary is { } code ? await intermediaries.WithSubBrokers(tenantId, code, clock.UtcToday, cancellationToken) : new HashSet<string>(), l.Provider)
            : null;
        // The functions the tenant allows now, and the permissions they require: the user's permissions (ADR-026 §2).
        var refusals = await Refusals(tenantId, configurationOnly, cancellationToken);
        var usable = granted.Where(g => refusals.TryGetValue(g.Function, out var refusal) && refusal == ErrorCodes.FunctionNotGranted).ToList();
        var functions = await catalogue.Functions(clock.UtcToday, cancellationToken);
        var scoped = usable.SelectMany(g => functions[g.Function].Permissions.Select(p => (Permission: p, g.Products, g.Branches))).ToList();
        var permissions = scoped.Select(s => s.Permission).ToHashSet(StringComparer.Ordinal);
        return new ResolvedPrincipal(userId, permissions, await Grants(tenantId, scoped, cancellationToken), roles, external, configurationOnly,
            new FunctionAccess(usable.Select(g => g.Function).ToHashSet(StringComparer.Ordinal), refusals));
    }

    /// <summary>
    /// Every function in force in the catalogue with the tenant's refusal, in the order of checks (ADR-026 §2): Onboarding,
    /// disabled, not entitled; FUNCTION_NOT_GRANTED for one the tenant allows. The operator tenant keeps platform data for
    /// every feature, so it needs no entitlement.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> Refusals(Guid tenantId, bool configurationOnly, CancellationToken cancellationToken)
    {
        var day = clock.UtcToday;
        var settings = await functionSettings.For(tenantId, day, cancellationToken);
        var isOperator = tenantId == platform.Value.OperatorTenantId;
        return (await catalogue.Functions(day, cancellationToken)).Values.ToDictionary(f => f.Code, f =>
            configurationOnly && !f.AllowedWhileOnboarding ? ErrorCodes.TenantNotActive
            : settings.DisabledFunctions.Contains(f.Code) ? ErrorCodes.FunctionDisabled
            : f.Feature is { } feature && !isOperator && !settings.EntitledFeatures.Contains(feature) ? ErrorCodes.FeatureNotEntitled
            : ErrorCodes.FunctionNotGranted, StringComparer.Ordinal);
    }

    /// <summary>
    /// Per permission, the grants of the assignments giving it (addenda/access-scopes.md §4). A permission any assignment
    /// gives without scope is left out: it is unrestricted. A branch scope covers the nodes below it (§3).
    /// </summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<PermissionGrant>>?> Grants(Guid tenantId,
        IReadOnlyList<(string Permission, string[]? Products, string[]? Branches)> scoped, CancellationToken cancellationToken)
    {
        static bool Empty(string[]? values) => values is null || values.Length == 0;
        if (scoped.All(s => Empty(s.Products) && Empty(s.Branches)))
        {
            return null;
        }

        var nodes = scoped.Any(s => !Empty(s.Branches)) ? await organisation.Nodes(tenantId, clock.UtcToday, cancellationToken) : [];
        var grants = new Dictionary<string, IReadOnlyList<PermissionGrant>>(StringComparer.Ordinal);
        foreach (var permission in scoped.GroupBy(s => s.Permission))
        {
            if (permission.Any(s => Empty(s.Products) && Empty(s.Branches)))
            {
                continue;
            }

            grants[permission.Key] = permission.Select(s => new PermissionGrant(
                Empty(s.Products) ? null : new HashSet<string>(s.Products!, StringComparer.Ordinal),
                Empty(s.Branches) ? null : OrganisationNodes.WithDescendants(nodes, s.Branches!))).ToList();
        }

        return grants;
    }
}

/// <summary>Drops cached permissions when a role or user changes anywhere, or the tenant is suspended (iam consumes TenantSuspended).</summary>
internal sealed class PermissionCacheInvalidation(IPermissionCache cache) :
    IIntegrationEventHandler<Contracts.RoleAssigned>,
    IIntegrationEventHandler<Contracts.SubjectLinkChanged>,
    IIntegrationEventHandler<Contracts.RoleRevoked>,
    IIntegrationEventHandler<Contracts.UserDisabled>,
    IIntegrationEventHandler<TenantSuspended>,
    IIntegrationEventHandler<TenantActivated>
{
    public Task Handle(TenantActivated integrationEvent, EventEnvelope envelope, CancellationToken cancellationToken) => Evict(envelope);

    public Task Handle(Contracts.RoleAssigned integrationEvent, EventEnvelope envelope, CancellationToken cancellationToken) => Evict(envelope);

    public Task Handle(Contracts.RoleRevoked integrationEvent, EventEnvelope envelope, CancellationToken cancellationToken) => Evict(envelope);

    public Task Handle(Contracts.UserDisabled integrationEvent, EventEnvelope envelope, CancellationToken cancellationToken) => Evict(envelope);

    public Task Handle(Contracts.SubjectLinkChanged integrationEvent, EventEnvelope envelope, CancellationToken cancellationToken) => Evict(envelope);

    public Task Handle(TenantSuspended integrationEvent, EventEnvelope envelope, CancellationToken cancellationToken) => Evict(envelope);

    private Task Evict(EventEnvelope envelope)
    {
        cache.EvictTenant(envelope.TenantId);
        return Task.CompletedTask;
    }
}

/// <summary>
/// ADR-026 §3: an activated role, role-function grant or function availability drops the tenant's cached principals; an
/// activated catalogue row, taxonomy row or feature entitlement (global) drops every tenant's, and the cached catalogue.
/// </summary>
internal sealed class FunctionCacheInvalidation(PermissionCache cache, FunctionCatalogueReader catalogue, FunctionAvailability availability)
    : IIntegrationEventHandler<BuildingBlocks.Contracts.StaticData.StaticDataVersionActivated>
{
    private static readonly HashSet<string> TenantTables = new(StringComparer.Ordinal)
    {
        "iam.cfg_roles_and_permission_sets", "iam.cfg_role_function_assignments", "tenant.cfg_tenant_function_availability",
    };

    private static readonly HashSet<string> GlobalTables = new(StringComparer.Ordinal)
    {
        "iam.ref_function_catalogue", "iam.ref_business_domains", "iam.ref_business_sub_domains", "tenant.cfg_feature_entitlements",
    };

    public Task Handle(BuildingBlocks.Contracts.StaticData.StaticDataVersionActivated activated, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var table = $"{activated.Record.Schema}.{activated.Record.Table}";
        if (GlobalTables.Contains(table))
        {
            catalogue.Evict();
            cache.EvictAll();
            availability.Evict(null);
        }
        else if (TenantTables.Contains(table))
        {
            cache.EvictTenant(envelope.TenantId);
            availability.Evict(envelope.TenantId);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Whether a portal client's authentication policy requires MFA (external-user access §6): a policy in force whose
/// client_ids hold the client and whose mfa_required is set. Read on Identity's own connection.
/// </summary>
internal sealed class PortalAuthentication([FromKeyedServices(IdentityDbContext.SchemaName)] NpgsqlDataSource dataSource) : IPortalAuthentication
{
    private static readonly string[] Statuses =
        IdentityStaticData.Descriptors.Single(d => d.EntityType == typeof(AuthenticationAndSessionPolicies)).Lifecycle.InForce;

    public async Task<bool> MfaRequired(Guid tenantId, string? clientId, DateOnly day, CancellationToken cancellationToken)
    {
        if (clientId is null)
        {
            return false;
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (SELECT 1 FROM iam.cfg_authentication_and_session_policies
                           WHERE (tenant_id = @t OR tenant_id IS NULL) AND @c = ANY(client_ids) AND mfa_required
                             AND record_status = ANY(@s) AND effective_from <= @day AND (effective_to IS NULL OR effective_to > @day))
            """, connection, transaction);
        command.Parameters.AddWithValue("t", tenantId);
        command.Parameters.AddWithValue("c", clientId);
        command.Parameters.AddWithValue("s", Statuses);
        command.Parameters.AddWithValue("day", day);
        var required = await command.ExecuteScalarAsync(cancellationToken) is true;
        await transaction.CommitAsync(cancellationToken);
        return required;
    }
}
