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
using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Integration.Infrastructure.Persistence;
using Insys.Integration.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;

namespace Insys.Integration.Infrastructure.Channels;

/// <summary>
/// The channel check (F005, docs/specs/addenda/channel-permissions.md), from channel.cfg_channel_definitions and
/// channel.cfg_channel_transaction_permissions in force in the tenant:
/// <list type="number">
/// <item>a tenant with no channel definitions has no channel scoping;</item>
/// <item>the request's channel is the one whose client_ids hold the token's client, in force; none is CHANNEL_NOT_ENABLED;</item>
/// <item>the channel's enabled_transactions (empty: all) must hold the transaction, the command's permission code;</item>
/// <item>the caller's roles must have a row with allowed = true for the channel and transaction; none is CHANNEL_NOT_ALLOWED.</item>
/// </list>
/// The products those rows allow, within enabled_products (empty: all), are checked where the command's product is known.
/// </summary>
internal sealed class ChannelPolicy(IntegrationDbContext db) : IChannelPolicy
{
    public async Task<ChannelDecision> Decide(Guid tenantId, string? clientId, string transactionType, IReadOnlyCollection<string> roleCodes, DateOnly day,
        CancellationToken cancellationToken)
    {
        // Its own read-only transaction, which sets the tenant for row-level security; the command's own unit of work follows.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var decision = await DecideInTenant(clientId, transactionType, roleCodes, day, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return decision;
    }

    public async Task<(string Code, string Audience)?> ChannelOf(Guid tenantId, string? clientId, DateOnly day, CancellationToken cancellationToken)
    {
        if (clientId is null)
        {
            return null;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var channel = (await InForce<ChannelDefinitions>(day).ToListAsync(cancellationToken))
            .FirstOrDefault(c => c.ClientIds?.Contains(clientId, StringComparer.Ordinal) == true);
        await transaction.CommitAsync(cancellationToken);
        return channel is null ? null : (channel.ChannelCode, channel.Audience);
    }

    private async Task<ChannelDecision> DecideInTenant(string? clientId, string transactionType, IReadOnlyCollection<string> roleCodes, DateOnly day,
        CancellationToken cancellationToken)
    {
        var channels = await InForce<ChannelDefinitions>(day).ToListAsync(cancellationToken);
        if (channels.Count == 0)
        {
            return ChannelDecision.NotScoped;
        }

        if (clientId is null || channels.FirstOrDefault(c => c.ClientIds?.Contains(clientId, StringComparer.Ordinal) == true) is not { } channel)
        {
            return ChannelDecision.Refused(ErrorCodes.ChannelNotEnabled, "The application you are using is not on a channel in force for this tenant.");
        }

        if (channel.EnabledTransactions is { Length: > 0 } transactions && !transactions.Contains(transactionType, StringComparer.Ordinal))
        {
            return ChannelDecision.Refused(ErrorCodes.ChannelNotEnabled, $"Channel {channel.ChannelCode} does not enable {transactionType}.", channel.ChannelCode);
        }

        var roles = roleCodes.ToArray();
        var rules = await InForce<ChannelTransactionPermissions>(day)
            .Where(p => p.ChannelCode == channel.ChannelCode && p.TransactionType == transactionType && p.Allowed && roles.Contains(p.RoleCode))
            .Select(p => new { p.ProductCode, p.RequiresApproval })
            .ToListAsync(cancellationToken);
        if (rules.Count == 0)
        {
            return ChannelDecision.Refused(ErrorCodes.ChannelNotAllowed, $"Your roles may not make {transactionType} on channel {channel.ChannelCode}.",
                channel.ChannelCode);
        }

        IReadOnlySet<string>? enabled = channel.EnabledProducts is { Length: > 0 } products ? products.ToHashSet(StringComparer.Ordinal) : null;
        return new ChannelDecision(channel.ChannelCode, null, null, enabled,
            rules.Select(r => new ChannelRule(string.IsNullOrEmpty(r.ProductCode) ? null : r.ProductCode, r.RequiresApproval == true)).ToList());
    }

    private IQueryable<T> InForce<T>(DateOnly day) where T : StaticRecord =>
        db.Set<T>().AsNoTracking().InForce(IntegrationStaticData.Descriptors.Single(d => d.EntityType == typeof(T)).Lifecycle, day);
}
