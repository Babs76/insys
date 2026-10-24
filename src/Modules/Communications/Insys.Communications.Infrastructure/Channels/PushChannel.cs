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
using Microsoft.Extensions.Logging;
using ChannelCodes = Insys.Communications.Contracts.Channels;

namespace Insys.Communications.Infrastructure.Channels;

/// <summary>The PUSH stub: a non-empty device token and a body of at most 1,000 characters.</summary>
internal sealed class PushChannel(StubChannelOutbox outbox, StubChannelSwitches switches, ILogger<PushChannel> logger)
    : StubNotificationChannel(outbox, switches, logger)
{
    public override string Channel => ChannelCodes.Push;

    protected override string? Validate(OutboundMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.Address))
            return ChannelFailures.AddressRejected;
        if ((message.Body?.Length ?? 0) > StubChannelRules.PushBodyMaxLength)
            return ChannelFailures.MessageRejected;
        return null;
    }
}
