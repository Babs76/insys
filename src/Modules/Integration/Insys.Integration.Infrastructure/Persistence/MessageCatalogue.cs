using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Localisation;
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
internal sealed class MessageCatalogue([FromKeyedServices(IntegrationDbContext.SchemaName)] NpgsqlDataSource dataSource, IMemoryCache cache, IClock clock)
    : IMessageCatalogue
{
    private const string DetailSuffix = ".detail";
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(1);

    public async Task<MessageTexts> Texts(Guid? tenantId, string language, CancellationToken cancellationToken)
    {
        var key = ("message-texts", tenantId, language);
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
        command.Parameters.AddWithValue("day", clock.UtcToday);
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
}
