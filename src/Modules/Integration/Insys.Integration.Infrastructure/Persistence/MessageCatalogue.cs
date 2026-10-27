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
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Localisation;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Contracts.StaticData;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.Integration.Infrastructure.Persistence;

/// <summary>
/// The API message catalogue (docs/specs/addenda/localisation.md §4, OQ-068): channel.ref_ui_label_and_translation_bundles
/// rows for every channel (channel_code empty), whose label_key is an error code's message_key ("title") or its
/// message_key plus ".detail". A tenant's own rows replace the global ones. Cached for a minute per tenant and language.
/// </summary>
public sealed class MessageCatalogue([FromKeyedServices(IntegrationDbContext.SchemaName)] NpgsqlDataSource dataSource, IMemoryCache cache, TenantBusinessDates dates)
    : IMessageCatalogue
{
    private const string DetailSuffix = ".detail";
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(1);
    private int _generation;

    public async Task<MessageTexts> Texts(Guid? tenantId, string language, CancellationToken cancellationToken)
    {
        var key = ("message-texts", Volatile.Read(ref _generation), tenantId, language);
        if (cache.TryGetValue(key, out MessageTexts? cached) && cached is not null)
        {
            return cached;
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (tenantId is { } tenant)
        {
            await TenantSession.SetTenant(connection, transaction, tenant, cancellationToken);
        }

        // Tenant rows sort after global ones, so they replace them when read into the dictionaries.
        await using var command = new NpgsqlCommand("""
            SELECT e.error_code, b.label_key, b.text
            FROM channel.ref_ui_label_and_translation_bundles b
            JOIN integration.ref_error_code_catalogue e
              ON b.label_key IN (e.message_key, e.message_key || '.detail')
             AND e.record_status = ANY(@statuses) AND e.effective_from <= @day AND (e.effective_to IS NULL OR e.effective_to > @day)
            WHERE b.language = @language AND b.channel_code IS NULL
              AND b.record_status = ANY(@statuses) AND b.effective_from <= @day AND (b.effective_to IS NULL OR b.effective_to > @day)
            ORDER BY b.tenant_id NULLS FIRST, b.version
            """, connection, transaction);
        command.Parameters.AddWithValue("statuses", StaticLifecycle.Reference.InForce);
        // A request with no tenant reads the global texts on the default zone's date.
        command.Parameters.AddWithValue("day", dates.TodayFor(tenantId ?? Guid.Empty));
        command.Parameters.AddWithValue("language", language);
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        var details = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var target = reader.GetString(1).EndsWith(DetailSuffix, StringComparison.Ordinal) ? details : titles;
                target[reader.GetString(0)] = reader.GetString(2);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        var texts = titles.Count == 0 && details.Count == 0 ? MessageTexts.None : new MessageTexts(titles, details);
        cache.Set(key, texts, CacheFor);
        return texts;
    }

    /// <summary>Every cached text is stale after a change to the catalogue, including a global one that serves all tenants.</summary>
    public void Invalidate() => Interlocked.Increment(ref _generation);
}

/// <summary>Drops the cached message texts when a translation bundle or an error code version is activated.</summary>
public sealed class MessageCachePolicy(MessageCatalogue catalogue) : IIntegrationEventHandler<StaticDataVersionActivated>
{
    public Task Handle(StaticDataVersionActivated activated, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (activated.Record is { Schema: "channel", Table: "ref_ui_label_and_translation_bundles" }
            or { Schema: IntegrationDbContext.SchemaName, Table: "ref_error_code_catalogue" })
        {
            catalogue.Invalidate();
        }

        return Task.CompletedTask;
    }
}
