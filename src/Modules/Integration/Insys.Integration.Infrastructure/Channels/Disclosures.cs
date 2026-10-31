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
internal sealed class Disclosures([FromKeyedServices(IntegrationDbContext.SchemaName)] NpgsqlDataSource dataSource) : IDisclosureGate, IDisclosures
{
    private const string InForce = "(tenant_id = @t OR tenant_id IS NULL) AND display_point = @point AND " + ReadinessSql.InForce;

    public Task<IReadOnlyList<string>> Outstanding(Guid tenantId, Guid partyId, string displayPoint, string? businessRefType, Guid? businessRefId, DateOnly day, CancellationToken cancellationToken) =>
        ReadinessSql.InTenant<IReadOnlyList<string>>(dataSource, tenantId, (connection, transaction) => ReadinessSql.Strings(connection, transaction,
            $"""
            SELECT DISTINCT notice_type FROM channel.cfg_disclosure_and_notice_texts t WHERE {InForce}
              AND NOT EXISTS (SELECT 1 FROM channel.disclosure_acknowledgement a WHERE a.tenant_id = @t AND a.party_id = @party AND a.notice_type = t.notice_type AND a.display_point = t.display_point
                AND a.text_version = t.text_version AND (@refId::uuid IS NULL OR (a.business_ref_type = @refType AND a.business_ref_id = @refId)))
            ORDER BY 1
            """, cancellationToken, ("t", tenantId), ("point", displayPoint), ("inForce", StaticLifecycle.Configuration.InForce), ("day", day), ("party", partyId),
            ("refId", (object?)businessRefId ?? DBNull.Value), ("refType", (object?)businessRefType ?? DBNull.Value)), cancellationToken);

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

    public Task Record(Guid tenantId, Guid id, Guid partyId, DisclosureTextView text, string? businessRefType, Guid? businessRefId, DateTimeOffset at, string? channelCode, string recordedBy, CancellationToken cancellationToken) =>
        ReadinessSql.InTenant(dataSource, tenantId, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO channel.disclosure_acknowledgement (tenant_id, id, party_id, notice_type, display_point, text_version, language, business_ref_type, business_ref_id, acknowledged_at, channel_code, recorded_by)
                VALUES (@t, @id, @party, @notice, @point, @version, @language, @refType, @refId, @at, @channel, @by)
                """, connection, transaction);
            command.Parameters.AddWithValue("t", tenantId);
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("party", partyId);
            command.Parameters.AddWithValue("notice", text.NoticeType);
            command.Parameters.AddWithValue("point", text.DisplayPoint);
            command.Parameters.AddWithValue("version", text.TextVersion);
            command.Parameters.AddWithValue("language", text.Language);
            command.Parameters.AddWithValue("refType", (object?)businessRefType ?? DBNull.Value);
            command.Parameters.AddWithValue("refId", (object?)businessRefId ?? DBNull.Value);
            command.Parameters.AddWithValue("at", at);
            command.Parameters.AddWithValue("channel", (object?)channelCode ?? DBNull.Value);
            command.Parameters.AddWithValue("by", recordedBy);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return true;
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
