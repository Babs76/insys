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

/// <summary>A message a stub channel accepted, with the reference it answered (addenda/communications.md §3).</summary>
public sealed record StubSentMessage(string Channel, string ProviderRef, OutboundMessage Message);

/// <summary>
/// What the stub channels have "sent", in memory, so that tests can assert on it (addenda/communications.md §3, OQ-088).
/// Thread-safe; keeps only the most recent <see cref="Capacity"/> messages per channel. It holds addresses and bodies in
/// memory only, for the life of the process, and never writes them anywhere.
/// </summary>
public sealed class StubChannelOutbox
{
    /// <summary>The number of messages kept per channel; older ones are dropped.</summary>
    public const int Capacity = 1_000;

    private readonly Dictionary<string, Queue<StubSentMessage>> _sent = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    internal void Record(StubSentMessage message)
    {
        lock (_gate)
        {
            if (!_sent.TryGetValue(message.Channel, out var queue))
            {
                queue = new Queue<StubSentMessage>();
                _sent[message.Channel] = queue;
            }

            queue.Enqueue(message);
            while (queue.Count > Capacity)
                queue.Dequeue();
        }
    }

    /// <summary>The messages sent on a channel, oldest first (a snapshot).</summary>
    public IReadOnlyList<StubSentMessage> Sent(string channel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        lock (_gate)
            return _sent.TryGetValue(channel, out var queue) ? [.. queue] : [];
    }

    /// <summary>Forgets every message on every channel.</summary>
    public void Clear()
    {
        lock (_gate)
            _sent.Clear();
    }
}
