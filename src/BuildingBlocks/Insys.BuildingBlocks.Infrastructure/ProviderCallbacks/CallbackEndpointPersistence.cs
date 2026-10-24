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
using Insys.BuildingBlocks.Application.ProviderCallbacks;
using Insys.BuildingBlocks.Domain.ProviderCallbacks;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Insys.BuildingBlocks.Infrastructure.ProviderCallbacks;

/// <summary>The mapping of a module's callback_endpoint table (ADR-023): the key held only as its hash, the secret only encrypted.</summary>
public static class CallbackEndpointModel
{
    public static void Configure(ModelBuilder modelBuilder, int providerCodeLength)
    {
        modelBuilder.Entity<CallbackEndpoint>(endpoint =>
        {
            endpoint.ToTable("callback_endpoint");
            endpoint.HasKey(e => new { e.TenantId, e.Id });
            endpoint.HasAuditColumns();
            endpoint.Ignore(e => e.DomainEvents);
            endpoint.Ignore(e => e.HasChanges);
            endpoint.Property(e => e.ProviderCode).HasMaxLength(providerCodeLength);
            endpoint.Property(e => e.KeyHash).HasMaxLength(64).IsFixedLength();
            endpoint.Property(e => e.Status).HasConversion(new UpperCaseEnumConverter<CallbackEndpointState>()).HasMaxLength(10);
            endpoint.Property(e => e.IssuedBy).HasMaxLength(80);
            endpoint.Property(e => e.RevokedBy).HasMaxLength(80);
        });
    }
}

/// <summary>A module's endpoints and receipts, in its own schema's callback_endpoint and callback_receipt tables.</summary>
public abstract class CallbackEndpointStore(ModuleDbContext db) : ICallbackEndpointStore
{
    private DbSet<CallbackEndpoint> Endpoints => db.Set<CallbackEndpoint>();

    public Task<CallbackEndpoint?> Get(Guid id, CancellationToken cancellationToken) => Endpoints.SingleOrDefaultAsync(e => e.Id == id, cancellationToken);

    public async Task<IReadOnlyList<CallbackEndpoint>> All(CancellationToken cancellationToken) =>
        await Endpoints.AsNoTracking().OrderBy(e => e.ProviderCode).ThenBy(e => e.Id).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<CallbackEndpoint>> Active(string providerCode, CancellationToken cancellationToken) =>
        await Endpoints.Where(e => e.ProviderCode == providerCode && e.Status == CallbackEndpointState.Active).ToListAsync(cancellationToken);

    public void Add(CallbackEndpoint endpoint) => Endpoints.Add(endpoint);

    // The schema is the module's own constant (ModuleDbContext.Schema), never input, so it is safe to place in the SQL.
#pragma warning disable EF1002
    public async Task<bool> TryRecordReceipt(Guid endpointId, string callbackId, DateTimeOffset now, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlRawAsync($$"""
            INSERT INTO {{db.Schema}}.callback_receipt (tenant_id, id, endpoint_id, callback_id, received_at)
            VALUES ({0}, {1}, {2}, {3}, {4})
            ON CONFLICT (tenant_id, endpoint_id, callback_id) DO NOTHING
            """, [db.CurrentTenantId, Guid.CreateVersion7(), endpointId, callbackId, now], cancellationToken) == 1;

    public Task PurgeReceipts(Guid endpointId, DateTimeOffset before, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlRawAsync($"DELETE FROM {db.Schema}.callback_receipt WHERE tenant_id = {{0}} AND endpoint_id = {{1}} AND received_at < {{2}}",
            [db.CurrentTenantId, endpointId, before], cancellationToken);
#pragma warning restore EF1002
}

/// <summary>
/// The key-hash lookup across tenants, before any tenant is known, through the module's
/// &lt;schema&gt;.resolve_callback_endpoint (V0033, V0051) on the module's own role.
/// </summary>
public static class CallbackEndpointLookup
{
    public static async Task<(Guid TenantId, Guid EndpointId)?> Resolve(NpgsqlDataSource dataSource, string schema, string keyHash, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"SELECT tenant_id, endpoint_id FROM {schema}.resolve_callback_endpoint(@hash)", connection, transaction);
        command.Parameters.AddWithValue("hash", keyHash);
        (Guid, Guid)? found = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                found = (reader.GetGuid(0), reader.GetGuid(1));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return found;
    }
}
