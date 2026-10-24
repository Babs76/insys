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
using System.Collections.Concurrent;
using Insys.Communications.Application.Channels;

namespace Insys.Communications.Infrastructure.Channels;

/// <summary>
/// The test switch of the stub channels (addenda/communications.md §3): a test sets a failure for a channel, and that
/// channel answers with it instead of sending until the switch is cleared. Thread-safe.
/// </summary>
public sealed class StubChannelSwitches
{
    private readonly ConcurrentDictionary<string, ChannelReceipt> _failures = new(StringComparer.Ordinal);

    /// <summary>Makes <paramref name="channel"/> answer <paramref name="failureCode"/> (a ChannelFailures code) to every send.</summary>
    public void Fail(string channel, string failureCode, bool retryable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        _failures[channel] = ChannelReceipt.Failed(failureCode, retryable);
    }

    /// <summary>Lets <paramref name="channel"/> send again.</summary>
    public void Clear(string channel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        _failures.TryRemove(channel, out _);
    }

    /// <summary>Lets every channel send again.</summary>
    public void ClearAll() => _failures.Clear();

    /// <summary>The failure set for <paramref name="channel"/>, or null when it sends normally.</summary>
    public ChannelReceipt? FailureFor(string channel) => _failures.TryGetValue(channel, out var failure) ? failure : null;
}
