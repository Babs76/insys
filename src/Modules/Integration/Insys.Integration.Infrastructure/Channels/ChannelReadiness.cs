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
using Insys.Identity.Contracts;
using Insys.Integration.Infrastructure.Persistence;
using Insys.Tenancy.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.Integration.Infrastructure.Channels;

/// <summary>
/// Onboarding readiness CHANNELS (addendum §5): a back-office channel (audience STAFF) whose client_ids list insys-web,
/// and for every staff role an allowed row on it for each channel-scoped function the role holds (ADR-026 §5). A row
/// with an empty product covers every product. Missing rows are named "ROLE: transaction".
/// OQ-165 (BR-140, Ade, 19 November 2026): for each portal the tenant is entitled to, a channel in force with that portal's audience, and for each role
/// granting that audience's functions an allowed row on it for each channel-scoped one, named "ROLE: transaction (AUDIENCE channel)".
/// </summary>
internal sealed class ChannelReadiness([FromKeyedServices(IntegrationDbContext.SchemaName)] NpgsqlDataSource dataSource, IStaffRoles roles,
    IChannelScopedTransactions transactions, IPortalRoles portalRoles, ITenantFunctionSettings settings) : IReadinessCheck
{
    public const string BackOfficeClient = "insys-web";

    /// <summary>Each portal feature (tenant.cfg_feature_entitlements) and the channel audience its users act through.</summary>
    public static readonly IReadOnlyList<(string Feature, string Audience)> Portals =
        [("CUSTOMER_PORTAL", "CUSTOMER"), ("BROKER_PORTAL", "BROKER"), ("PROVIDER_PORTAL", "PROVIDER")];

    public IReadOnlyCollection<string> Steps { get; } = ["ORGANISATION_AND_ACCESS"];

    public async Task<IReadOnlyList<ReadinessResult>> Check(ReadinessContext context, CancellationToken cancellationToken)
    {
        var staff = await roles.InForce(context.TenantId, context.Day, cancellationToken);
        var entitled = (await settings.For(context.TenantId, context.Day, cancellationToken)).EntitledFeatures;
        var portals = Portals.Where(p => entitled.Contains(p.Feature)).ToList();
        IReadOnlyList<(string RoleCode, string Audience, IReadOnlyList<string> Functions)> granting =
            portals.Count == 0 ? [] : await portalRoles.InForce(context.TenantId, context.Day, cancellationToken);
        var (backOffice, rows, portalChannels, portalRows) = await ReadinessSql.InTenant(dataSource, context.TenantId, async (connection, transaction) =>
        {
            var channels = await ReadinessSql.Strings(connection, transaction, $"""
                SELECT channel_code FROM channel.cfg_channel_definitions
                WHERE tenant_id = @t AND {ReadinessSql.InForce} AND audience = 'STAFF' AND @client = ANY(client_ids) ORDER BY channel_code
                """, cancellationToken, ("t", context.TenantId), ("inForce", StaticLifecycle.Configuration.InForce), ("day", context.Day), ("client", BackOfficeClient));
            var allowed = channels.Count == 0
                ? []
                : await ReadinessSql.Strings(connection, transaction, $"""
                    SELECT role_code || ' ' || function_code FROM channel.cfg_channel_transaction_permissions
                    WHERE tenant_id = @t AND {ReadinessSql.InForce} AND channel_code = ANY(@channels) AND allowed AND function_code IS NOT NULL
                    """, cancellationToken, ("t", context.TenantId), ("inForce", StaticLifecycle.Configuration.InForce), ("day", context.Day),
                    ("channels", channels.ToArray()));
            var byAudience = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            var allowedByAudience = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var (_, audience) in portals)
            {
                var ofAudience = await ReadinessSql.Strings(connection, transaction, $"""
                    SELECT channel_code FROM channel.cfg_channel_definitions
                    WHERE tenant_id = @t AND {ReadinessSql.InForce} AND audience = @audience ORDER BY channel_code
                    """, cancellationToken, ("t", context.TenantId), ("inForce", StaticLifecycle.Configuration.InForce), ("day", context.Day), ("audience", audience));
                byAudience[audience] = ofAudience;
                allowedByAudience[audience] = ofAudience.Count == 0
                    ? []
                    : (await ReadinessSql.Strings(connection, transaction, $"""
                        SELECT role_code || ' ' || function_code FROM channel.cfg_channel_transaction_permissions
                        WHERE tenant_id = @t AND {ReadinessSql.InForce} AND channel_code = ANY(@channels) AND allowed AND function_code IS NOT NULL
                        """, cancellationToken, ("t", context.TenantId), ("inForce", StaticLifecycle.Configuration.InForce), ("day", context.Day),
                        ("channels", ofAudience.ToArray()))).ToHashSet(StringComparer.Ordinal);
            }

            return (channels, allowed.ToHashSet(StringComparer.Ordinal), byAudience, allowedByAudience);
        }, cancellationToken);

        var missing = new List<string>();
        if (backOffice.Count == 0)
        {
            missing.Add($"a back-office channel (audience STAFF) with {BackOfficeClient} in client_ids");
        }

        foreach (var (role, functions) in staff)
        {
            missing.AddRange(functions.Where(transactions.All.Contains).Where(p => !rows.Contains($"{role} {p}")).Order(StringComparer.Ordinal)
                .Select(p => $"{role}: {p}"));
        }

        foreach (var (feature, audience) in portals)
        {
            if (portalChannels[audience].Count == 0)
            {
                missing.Add($"a {audience} channel (audience {audience}), as the tenant has {feature}");
                continue;
            }

            foreach (var (role, _, functions) in granting.Where(r => r.Audience == audience))
            {
                missing.AddRange(functions.Where(transactions.All.Contains).Where(p => !portalRows[audience].Contains($"{role} {p}")).Order(StringComparer.Ordinal)
                    .Select(p => $"{role}: {p} ({audience} channel)"));
            }
        }

        return [ReadinessResult.Of("ORGANISATION_AND_ACCESS", "CHANNELS",
            "A back-office channel listing insys-web, with an allowed row for each staff role's channel-scoped functions (F005, ADR-026)", missing)];
    }
}
