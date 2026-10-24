/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Application
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
using Insys.BuildingBlocks.Domain;

namespace Insys.BuildingBlocks.Application.Context;

/// <summary>One permission row of the channel for the caller's roles: allowed for a product (null: every product), with or without approval.</summary>
public sealed record ChannelRule(string? ProductCode, bool RequiresApproval);

/// <summary>
/// What the request's channel allows for one transaction (F005, docs/specs/addenda/channel-permissions.md): not scoped
/// (the tenant has no channels), refused outright, or allowed for some products.
/// </summary>
/// <param name="ChannelCode">The channel the request's client is on; null when not scoped or when no channel was found.</param>
/// <param name="EnabledProducts">The channel's enabled products; null for every product.</param>
/// <param name="Rules">The allowed rows for the caller's roles, this channel and this transaction.</param>
public sealed record ChannelDecision(string? ChannelCode, string? RefusalCode, string? RefusalMessage, IReadOnlySet<string>? EnabledProducts, IReadOnlyList<ChannelRule> Rules)
{
    public static readonly ChannelDecision NotScoped = new(null, null, null, null, []);

    public bool IsScoped => ChannelCode is not null || RefusalCode is not null;

    public static ChannelDecision Refused(string code, string message, string? channelCode = null) => new(channelCode, code, message, null, []);
}

/// <summary>
/// The channel check for channel-scoped commands (F005 §3). Read from the Integration module's channel tables. The
/// transaction type is the command's permission code (§2); the channel comes from the token's OAuth client (§1).
/// </summary>
public interface IChannelPolicy
{
    Task<ChannelDecision> Decide(Guid tenantId, string? clientId, string transactionType, IReadOnlyCollection<string> roleCodes, DateOnly day,
        CancellationToken cancellationToken);
}

/// <summary>
/// The products a request may act on through its channel (F005 §3): those the channel enables and the caller's roles are
/// allowed, without approval unless the command has an approval flow. A product allowed only with approval, on a command
/// without one, is refused with CHANNEL_APPROVAL_REQUIRED.
/// </summary>
public sealed class ChannelGate
{
    private readonly bool _allProducts;
    private readonly HashSet<string> _products = new(StringComparer.Ordinal);
    private readonly bool _approvalForAll;
    private readonly HashSet<string> _approvalOnly = new(StringComparer.Ordinal);
    private readonly IReadOnlySet<string>? _enabled;

    public ChannelGate(ChannelDecision decision, bool hasApprovalFlow)
    {
        ChannelCode = decision.ChannelCode;
        _enabled = decision.EnabledProducts;
        foreach (var rule in decision.Rules.Where(r => hasApprovalFlow || !r.RequiresApproval))
        {
            if (rule.ProductCode is null) _allProducts = true; else _products.Add(rule.ProductCode);
        }

        foreach (var rule in decision.Rules.Where(r => !hasApprovalFlow && r.RequiresApproval))
        {
            if (rule.ProductCode is null) _approvalForAll = true; else _approvalOnly.Add(rule.ProductCode);
        }
    }

    public string? ChannelCode { get; }

    /// <summary>Every product allowed, so the channel does not narrow the scope.</summary>
    public bool AllowsEveryProduct => _allProducts && _enabled is null;

    /// <summary>The products allowed; meaningful when <see cref="AllowsEveryProduct"/> is false.</summary>
    public IReadOnlySet<string> Products =>
        _allProducts ? _enabled!.ToHashSet(StringComparer.Ordinal) : _products.Where(p => _enabled is null || _enabled.Contains(p)).ToHashSet(StringComparer.Ordinal);

    /// <summary>Why the command is refused whatever its product: no product is allowed without approval, or none at all.</summary>
    public (string Code, string Message)? RefusalForEveryProduct =>
        _allProducts || _products.Count > 0 ? null
        : _approvalForAll || _approvalOnly.Count > 0 ? (ErrorCodes.ChannelApprovalRequired, $"Channel {ChannelCode} requires approval for this transaction, and it has no approval flow.")
        : (ErrorCodes.ChannelNotAllowed, $"Your roles may not make this transaction on channel {ChannelCode}.");

    public bool Allows(string? product) => AllowsEveryProduct || (product is not null && Products.Contains(product));

    /// <summary>Why the channel refuses the product, or null when it allows it.</summary>
    public (string Code, string Message)? Refusal(string? product)
    {
        if (Allows(product))
        {
            return null;
        }

        var enabled = product is not null && (_enabled is null || _enabled.Contains(product));
        return enabled && (_approvalForAll || _approvalOnly.Contains(product!))
            ? (ErrorCodes.ChannelApprovalRequired, $"Channel {ChannelCode} requires approval for this transaction on {product}, and it has no approval flow.")
            : (ErrorCodes.ChannelNotAllowed, $"Your roles may not make this transaction for {product ?? "this record"} on channel {ChannelCode}.");
    }
}
