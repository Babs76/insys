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

namespace Insys.Communications.Infrastructure.Channels;

/// <summary>
/// What every Wave 1 stub channel does (addenda/communications.md §3, OQ-088): validate the message as the channel's
/// provider would, answer the test switch's failure if one is set, otherwise record the message in the outbox and
/// answer Sent with a <c>STUB-</c> reference.
/// Logs carry only the channel, the correlation ID, the provider reference and a failure code, never the address,
/// subject or body (§10, POPIA).
/// </summary>
internal abstract class StubNotificationChannel(StubChannelOutbox outbox, StubChannelSwitches switches, ILogger logger) : INotificationChannel
{
    public const string ProviderRefPrefix = "STUB-";

    public abstract string Channel { get; }

    public Task<ChannelReceipt> Send(OutboundMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();

        var rejection = Validate(message);
        if (rejection is not null)
            return Task.FromResult(Refused(message, ChannelReceipt.Failed(rejection, retryable: false)));

        var failure = switches.FailureFor(Channel);
        if (failure is not null)
            return Task.FromResult(Refused(message, failure));

        var providerRef = ProviderRefPrefix + Guid.NewGuid().ToString("N");
        outbox.Record(new StubSentMessage(Channel, providerRef, message));
        logger.LogInformation("{Channel} stub accepted message {CorrelationId} as {ProviderRef}", Channel, message.CorrelationId, providerRef);
        return Task.FromResult(ChannelReceipt.Sent(providerRef));
    }

    /// <summary>The ChannelFailures code the provider would refuse the message with, or null when it is acceptable.</summary>
    protected abstract string? Validate(OutboundMessage message);

    private ChannelReceipt Refused(OutboundMessage message, ChannelReceipt receipt)
    {
        logger.LogInformation("{Channel} stub refused message {CorrelationId}: {FailureCode} (retryable {Retryable})",
            Channel, message.CorrelationId, receipt.FailureCode, receipt.Retryable);
        return receipt;
    }
}
