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
namespace Insys.Communications.Application.Channels;

/// <summary>
/// One channel's sender (addenda/communications.md §3): email, SMS, push or WhatsApp. Each is an adapter behind this
/// interface; Wave 1 ships stubs, and a tenant's provider is chosen later (OQ-088). An adapter sends exactly what it is
/// given and nothing else; it never logs the address or the body.
/// </summary>
public interface INotificationChannel
{
    /// <summary>The channel code (Contracts.Channels).</summary>
    string Channel { get; }

    Task<ChannelReceipt> Send(OutboundMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// What a channel sends. <paramref name="Address"/> is the email address, the E.164 mobile number, or the push token,
/// read from Party (or the device registration) when sending; it is not stored.
/// </summary>
/// <param name="CorrelationId">The delivery attempt's ID, which a provider echoes back in its delivery callback.</param>
/// <param name="ProviderCode">The tenant's provider for the channel (cfg_channel_providers).</param>
/// <param name="SenderIdentity">The sender shown: an email address, SMS sender ID or WhatsApp business name.</param>
/// <param name="Subject">Email and push only; null on SMS and WhatsApp.</param>
public sealed record OutboundMessage(Guid CorrelationId, Guid TenantId, string ProviderCode, string SenderIdentity, string Address, string? Subject, string Body);

/// <summary>A channel's answer.</summary>
/// <param name="Accepted">The provider took the message; delivery is confirmed later by callback.</param>
/// <param name="ProviderRef">The provider's message reference, matched by the delivery callback.</param>
/// <param name="FailureCode">Why it was not accepted, as a stable code (ChannelFailures), never provider text with personal data.</param>
/// <param name="Retryable">A temporary failure, worth retrying on the same channel.</param>
public sealed record ChannelReceipt(bool Accepted, string? ProviderRef, string? FailureCode, bool Retryable)
{
    public static ChannelReceipt Sent(string providerRef) => new(true, providerRef, null, false);

    public static ChannelReceipt Failed(string failureCode, bool retryable) => new(false, null, failureCode, retryable);
}

/// <summary>The failure codes channels answer with.</summary>
public static class ChannelFailures
{
    /// <summary>The provider could not be reached or timed out: retry.</summary>
    public const string ProviderUnavailable = "PROVIDER_UNAVAILABLE";

    /// <summary>The provider refused the address (malformed, unreachable, unsubscribed): the next channel.</summary>
    public const string AddressRejected = "ADDRESS_REJECTED";

    /// <summary>The provider refused the message (too long, content rule): the next channel.</summary>
    public const string MessageRejected = "MESSAGE_REJECTED";

    /// <summary>The provider is throttling: retry.</summary>
    public const string RateLimited = "RATE_LIMITED";
}

/// <summary>The channels registered for this process, by code.</summary>
public interface INotificationChannels
{
    INotificationChannel? For(string channel);
}
