/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Communications
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
using Insys.Communications.Application.Channels;

namespace Insys.Communications.Infrastructure.Channels;

/// <summary>The registered channels by code (addenda/communications.md §3); an unknown code answers null.</summary>
public sealed class NotificationChannelRegistry : INotificationChannels
{
    private readonly Dictionary<string, INotificationChannel> _channels = new(StringComparer.Ordinal);

    public NotificationChannelRegistry(IEnumerable<INotificationChannel> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        foreach (var channel in channels)
        {
            if (!_channels.TryAdd(channel.Channel, channel))
                throw new InvalidOperationException($"More than one notification channel is registered for {channel.Channel}.");
        }
    }

    public INotificationChannel? For(string channel) =>
        channel is not null && _channels.TryGetValue(channel, out var found) ? found : null;
}
