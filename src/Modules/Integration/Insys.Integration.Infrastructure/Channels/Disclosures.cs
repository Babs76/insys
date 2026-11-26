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
using Insys.BuildingBlocks.Application.Localisation;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Infrastructure.Readiness;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Integration.Application;
using Insys.Integration.Contracts;
using Insys.Integration.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

using Insys.ProductFactory.Contracts;
using Insys.Tenancy.Contracts;

namespace Insys.Integration.Infrastructure.Channels;

/// <summary>F060: the texts in force (channel.cfg_disclosure_and_notice_texts) and who acknowledged them (channel.disclosure_acknowledgement). Runs on Integration's own connection.</summary>
internal sealed class Disclosures([FromKeyedServices(IntegrationDbContext.SchemaName)] NpgsqlDataSource dataSource, Party.Contracts.IPartyDirectory parties, Party.Contracts.IPartyLanguage partyLanguage,
    ILanguageSettings languages, ITenantFormatting locale) : IDisclosureGate, IDisclosures
{
    private const string InForce = "(tenant_id = @t OR tenant_id IS NULL) AND display_point = @point AND " + ReadinessSql.InForce;

    /// <summary>
    /// The texts of the party's country: its address's, else its nationality's, else the tenant's. With none of those, every country's. A notice type in a jurisdiction is
    /// acknowledged when its text in force in one of its languages is, in that version; otherwise it is outstanding, named in the language DisclosureLanguage chooses, if any (DE-039).
    /// </summary>
    public async Task<IReadOnlyList<DisclosureNotice>> Outstanding(Guid tenantId, Guid partyId, string displayPoint, string? businessRefType, Guid? businessRefId, DateOnly day, CancellationToken cancellationToken)
    {
        var country = await parties.CountryOf(tenantId, partyId, cancellationToken) ?? (await locale.Formatting(tenantId, day, cancellationToken))?.Country;
        var texts = await ReadinessSql.InTenant(dataSource, tenantId, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand(
                $"""
                SELECT t.notice_type, t.jurisdiction, t.language, t.text_version, t.tenant_id IS NULL,
                  EXISTS (SELECT 1 FROM channel.disclosure_acknowledgement a WHERE a.tenant_id = @t AND a.party_id = @party AND a.notice_type = t.notice_type AND a.display_point = t.display_point
                    AND a.text_version = t.text_version AND a.language = t.language AND (a.jurisdiction IS NULL OR a.jurisdiction = t.jurisdiction)
                    AND (@refId::uuid IS NULL OR (a.business_ref_type = @refType AND a.business_ref_id = @refId)))
                FROM channel.cfg_disclosure_and_notice_texts t WHERE {InForce} AND (@country::text IS NULL OR t.jurisdiction = @country)
                """, connection, transaction);
            command.Parameters.AddWithValue("t", tenantId);
            command.Parameters.AddWithValue("point", displayPoint);
            command.Parameters.AddWithValue("inForce", StaticLifecycle.Configuration.InForce);
            command.Parameters.AddWithValue("day", day);
            command.Parameters.AddWithValue("party", partyId);
            command.Parameters.AddWithValue("country", (object?)country ?? DBNull.Value);
            command.Parameters.AddWithValue("refId", (object?)businessRefId ?? DBNull.Value);
            command.Parameters.AddWithValue("refType", (object?)businessRefType ?? DBNull.Value);
            var rows = new List<(string Notice, string Jurisdiction, string Language, int Version, bool Global, bool Acknowledged)>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetBoolean(4), reader.GetBoolean(5)));
            }

            return rows;
        }, cancellationToken);

        // Ade, 25 November 2026: a notice counts as acknowledged once its text in force in any of its languages is acknowledged, in that version; a new version in force
        // needs a new acknowledgement. Per language, the text in force is the tenant's own over a global one, then the highest version, as Text reads it.
        var open = texts.GroupBy(r => (r.Notice, r.Jurisdiction))
            .Select(g => (g.Key, InForce: g.GroupBy(r => r.Language, StringComparer.Ordinal).Select(l => l.OrderBy(r => r.Global).ThenByDescending(r => r.Version).First()).ToList()))
            .Where(g => !g.InForce.Any(r => r.Acknowledged)).OrderBy(g => g.Key.Notice, StringComparer.Ordinal).ThenBy(g => g.Key.Jurisdiction, StringComparer.Ordinal).ToList();
        if (open.Count == 0)
        {
            return [];
        }

        var preferred = await partyLanguage.PreferredLanguage(tenantId, partyId, cancellationToken);
        var tenantDefault = (await languages.For(tenantId, cancellationToken)).Default;
        return open.Select(g =>
        {
            var available = g.InForce.Select(r => r.Language).Order(StringComparer.Ordinal).ToList();
            var language = DisclosureLanguage.Choose(available, preferred, tenantDefault);
            int? version = language is null ? null : g.InForce.Single(r => r.Language == language).Version;
            return new DisclosureNotice(g.Key.Notice, g.Key.Jurisdiction, language, version, available);
        }).ToList();
    }

    public Task<DisclosureTextView?> Text(Guid tenantId, string displayPoint, string noticeType, string jurisdiction, string language, DateOnly day, CancellationToken cancellationToken) =>
        ReadinessSql.InTenant(dataSource, tenantId, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand(
                $"SELECT notice_type, display_point, jurisdiction, language, text_version, text FROM channel.cfg_disclosure_and_notice_texts WHERE {InForce} AND notice_type = @notice AND jurisdiction = @jurisdiction AND language = @language ORDER BY tenant_id NULLS LAST LIMIT 1",
                connection, transaction);
            command.Parameters.AddWithValue("t", tenantId);
            command.Parameters.AddWithValue("point", displayPoint);
            command.Parameters.AddWithValue("inForce", StaticLifecycle.Configuration.InForce);
            command.Parameters.AddWithValue("day", day);
            command.Parameters.AddWithValue("notice", noticeType);
            command.Parameters.AddWithValue("jurisdiction", jurisdiction);
            command.Parameters.AddWithValue("language", language);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken)
                ? new DisclosureTextView(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4), reader.GetString(5))
                : null;
        }, cancellationToken);

    public Task Record(Guid tenantId, Guid id, Guid partyId, DisclosureTextView text, string? businessRefType, Guid? businessRefId, DateTimeOffset at, AcknowledgementRecorder recorder,
        CancellationToken cancellationToken) =>
        ReadinessSql.InTenant(dataSource, tenantId, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO channel.disclosure_acknowledgement (tenant_id, id, party_id, notice_type, display_point, text_version, language, jurisdiction, business_ref_type, business_ref_id, acknowledged_at, channel_code, recorded_by,
                  intermediary_code, presentation_method, presentation_detail)
                VALUES (@t, @id, @party, @notice, @point, @version, @language, @jurisdiction, @refType, @refId, @at, @channel, @by, @broker, @method, @detail)
                """, connection, transaction);
            command.Parameters.AddWithValue("t", tenantId);
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("party", partyId);
            command.Parameters.AddWithValue("notice", text.NoticeType);
            command.Parameters.AddWithValue("point", text.DisplayPoint);
            command.Parameters.AddWithValue("version", text.TextVersion);
            command.Parameters.AddWithValue("language", text.Language);
            command.Parameters.AddWithValue("jurisdiction", text.Jurisdiction);
            command.Parameters.AddWithValue("refType", (object?)businessRefType ?? DBNull.Value);
            command.Parameters.AddWithValue("refId", (object?)businessRefId ?? DBNull.Value);
            command.Parameters.AddWithValue("at", at);
            command.Parameters.AddWithValue("channel", (object?)recorder.ChannelCode ?? DBNull.Value);
            command.Parameters.AddWithValue("by", recorder.RecordedBy);
            command.Parameters.AddWithValue("broker", (object?)recorder.IntermediaryCode ?? DBNull.Value);
            command.Parameters.AddWithValue("method", (object?)recorder.PresentationMethod ?? DBNull.Value);
            command.Parameters.AddWithValue("detail", (object?)recorder.PresentationDetail ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }, cancellationToken);

    /// <summary>DE-039: a broker's has its code; the customer's own came through the member portal (channel PARTY); any other, staff.</summary>
    public Task<IReadOnlyList<PortalDisclosureAcknowledgementView>> Acknowledgements(Guid tenantId, Guid partyId, CancellationToken cancellationToken) =>
        ReadinessSql.InTenant<IReadOnlyList<PortalDisclosureAcknowledgementView>>(dataSource, tenantId, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT id, notice_type, display_point, jurisdiction, language, text_version, business_ref_type, business_ref_id, acknowledged_at, channel_code, intermediary_code,
                  presentation_method, presentation_detail
                FROM channel.disclosure_acknowledgement WHERE tenant_id = @t AND party_id = @party ORDER BY acknowledged_at DESC, id DESC
                """, connection, transaction);
            command.Parameters.AddWithValue("t", tenantId);
            command.Parameters.AddWithValue("party", partyId);
            var rows = new List<PortalDisclosureAcknowledgementView>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                string? Opt(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
                var broker = Opt(10);
                var recordedBy = broker is not null ? DisclosureRecorders.Broker : Opt(9) == ExternalSubject.Party ? DisclosureRecorders.Self : DisclosureRecorders.Staff;
                rows.Add(new PortalDisclosureAcknowledgementView(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), Opt(3), reader.GetString(4), reader.GetInt32(5), Opt(6),
                    reader.IsDBNull(7) ? null : reader.GetGuid(7), reader.GetFieldValue<DateTimeOffset>(8), recordedBy, broker, Opt(11), Opt(12)));
            }

            return rows;
        }, cancellationToken);

    public async Task<string?> ChannelOf(Guid tenantId, string? clientId, DateOnly day, CancellationToken cancellationToken) =>
        clientId is null
            ? null
            : (await ReadinessSql.InTenant(dataSource, tenantId, (connection, transaction) => ReadinessSql.Strings(connection, transaction,
                $"SELECT channel_code FROM channel.cfg_channel_definitions WHERE tenant_id = @t AND {ReadinessSql.InForce} AND @client = ANY(client_ids) ORDER BY channel_code",
                cancellationToken, ("t", tenantId), ("inForce", StaticLifecycle.Configuration.InForce), ("day", day), ("client", clientId)), cancellationToken)).FirstOrDefault();

    /// <summary>OQ-201: with no row, brokers may record (Ade's beta default).</summary>
    public Task<DisclosureSettingView> Setting(Guid tenantId, CancellationToken cancellationToken) =>
        ReadinessSql.InTenant(dataSource, tenantId, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("SELECT broker_acknowledgement_allowed, row_version FROM channel.disclosure_setting WHERE tenant_id = @t", connection, transaction);
            command.Parameters.AddWithValue("t", tenantId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? new DisclosureSettingView(reader.GetBoolean(0), false, reader.GetInt64(1)) : new DisclosureSettingView(true, true, 0);
        }, cancellationToken);

    public Task<long?> SaveSetting(Guid tenantId, bool brokerAcknowledgementAllowed, long? expectedVersion, string by, DateTimeOffset at, CancellationToken cancellationToken) =>
        ReadinessSql.InTenant(dataSource, tenantId, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand(expectedVersion is null
                ? """
                  INSERT INTO channel.disclosure_setting (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, broker_acknowledgement_allowed)
                  VALUES (@t, @id, 1, @at, @by, @at, @by, @allowed) ON CONFLICT (tenant_id) DO NOTHING RETURNING row_version
                  """
                : """
                  UPDATE channel.disclosure_setting SET broker_acknowledgement_allowed = @allowed, row_version = row_version + 1, updated_at = @at, updated_by = @by
                  WHERE tenant_id = @t AND row_version = @version RETURNING row_version
                  """, connection, transaction);
            command.Parameters.AddWithValue("t", tenantId);
            command.Parameters.AddWithValue("id", Guid.CreateVersion7());
            command.Parameters.AddWithValue("at", at);
            command.Parameters.AddWithValue("by", by.Length > 80 ? by[..80] : by);
            command.Parameters.AddWithValue("allowed", brokerAcknowledgementAllowed);
            command.Parameters.AddWithValue("version", expectedVersion ?? 0L);
            return await command.ExecuteScalarAsync(cancellationToken) as long?;
        }, cancellationToken);
}

/// <summary>
/// Readiness for disclosures (OQ-157, Ade, 31 October 2026). DISCLOSURE_MANDATORY_TEXTS blocks: every country a sellable product is sold in has a text in force at each mandatory point. A product
/// whose availability rules name no country is sold in the tenant's own. DISCLOSURE_TEXTS warns of the other points with no text anywhere.
/// </summary>
internal sealed class DisclosureReadiness([FromKeyedServices(IntegrationDbContext.SchemaName)] NpgsqlDataSource dataSource, IPublishedProducts products, ITenantFormatting locale) : IReadinessCheck
{
    private static readonly string[] Mandatory = [DisclosurePoints.Quote, DisclosurePoints.Inception, DisclosurePoints.Renewal, DisclosurePoints.ClaimDecision];
    private static readonly string[] Others = ["PROPOSAL"];

    public IReadOnlyCollection<string> Steps { get; } = ["INTEGRATION_AND_REPORTING"];

    public async Task<IReadOnlyList<ReadinessResult>> Check(ReadinessContext context, CancellationToken cancellationToken)
    {
        var present = await ReadinessSql.InTenant(dataSource, context.TenantId, (connection, transaction) => ReadinessSql.Strings(connection, transaction,
            $"SELECT DISTINCT jurisdiction || ' ' || display_point FROM channel.cfg_disclosure_and_notice_texts WHERE (tenant_id = @t OR tenant_id IS NULL) AND {ReadinessSql.InForce}", cancellationToken,
            ("t", context.TenantId), ("inForce", StaticLifecycle.Configuration.InForce), ("day", context.Day)), cancellationToken);
        var home = (await locale.Formatting(context.TenantId, context.Day, cancellationToken))?.Country;
        var soldIn = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var (product, _) in await products.Sellable(context.TenantId, context.Day, cancellationToken))
        {
            var countries = await products.SaleCountries(context.TenantId, product, context.Day, cancellationToken);
            foreach (var country in countries.Count > 0 ? countries : home is null ? [] : [home])
            {
                if (!soldIn.TryGetValue(country, out var sold))
                {
                    soldIn[country] = sold = new SortedSet<string>(StringComparer.Ordinal);
                }

                sold.Add(product);
            }
        }

        var missing = soldIn.SelectMany(c => Mandatory.Where(point => !present.Contains($"{c.Key} {point}")).Select(point => $"{c.Key} {point}: {string.Join(", ", c.Value)}")).ToList();
        var points = present.Select(p => p[(p.IndexOf(' ', StringComparison.Ordinal) + 1)..]).ToHashSet(StringComparer.Ordinal);
        return
        [
            ReadinessResult.Of("INTEGRATION_AND_REPORTING", "DISCLOSURE_MANDATORY_TEXTS", "A text in force at each mandatory disclosure point, for each country a product is sold in (OQ-157)", missing),
            ReadinessResult.Warning("INTEGRATION_AND_REPORTING", "DISCLOSURE_TEXTS", "A disclosure point with no text in force isn't enforced", Others.Where(p => !points.Contains(p)).ToList()),
        ];
    }
}
