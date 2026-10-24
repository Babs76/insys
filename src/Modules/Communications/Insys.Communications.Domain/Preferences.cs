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
using Insys.BuildingBlocks.Domain;

namespace Insys.Communications.Domain;

/// <summary>A party opted out of a marketing type: Party records the consent withdrawal too (§4).</summary>
public sealed record MarketingOptedOut(Guid PartyId, string TypeCode, string ConsentPurpose, string CaptureChannel, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// A party's channels for one notification type, in order, or opted out (addenda/communications.md §4). A service
/// notice keeps at least one channel; only marketing can be opted out of (OQ-087).
/// </summary>
public sealed class NotificationPreference : AggregateRoot<Guid>
{
    private NotificationPreference()
    {
    }

    public Guid PartyId { get; private set; }

    public string TypeCode { get; private set; } = "";

    public IReadOnlyList<string> Channels { get; private set; } = [];

    public bool OptedOut { get; private set; }

    public static Result<NotificationPreference> Set(NotificationPreference? current, Guid partyId, string typeCode, string? consentPurpose,
        IReadOnlyCollection<string> allowedChannels, IReadOnlyList<string> channels, bool optedOut, string captureChannel, DateTimeOffset now)
    {
        if (optedOut && consentPurpose is null)
        {
            return Error.Validation(CommunicationErrorCodes.PreferenceNotAllowed, $"{typeCode} is a service notice: it keeps at least one channel and cannot be opted out of.");
        }

        if (!optedOut && channels.Count == 0)
        {
            return Error.Validation(CommunicationErrorCodes.PreferenceNotAllowed, "Name at least one channel, or opt out of a marketing type.");
        }

        if (channels.FirstOrDefault(c => !allowedChannels.Contains(c)) is { } notAllowed)
        {
            return Error.Validation(CommunicationErrorCodes.PreferenceNotAllowed, $"{typeCode} is not sent on {notAllowed}.");
        }

        if (channels.Distinct(StringComparer.Ordinal).Count() != channels.Count)
        {
            return Error.Validation(CommunicationErrorCodes.PreferenceNotAllowed, "A channel is named twice.");
        }

        var preference = current ?? new NotificationPreference { Id = Guid.CreateVersion7(), PartyId = Guard.NotEmpty(partyId), TypeCode = Guard.MaxLength(typeCode, 30) };
        if (optedOut && !preference.OptedOut)
        {
            preference.Raise(new MarketingOptedOut(preference.PartyId, preference.TypeCode, consentPurpose!, captureChannel, now));
        }

        preference.Channels = optedOut ? [] : [.. channels];
        preference.OptedOut = optedOut;
        preference.MarkChanged();
        return preference;
    }

    /// <summary>
    /// The channels to try for this party: their order within the type's, or the type's when they named none it still
    /// has. Empty when they opted out.
    /// </summary>
    public static IReadOnlyList<string> Order(NotificationPreference? preference, IReadOnlyList<string> typeChannels)
    {
        if (preference is null)
        {
            return typeChannels;
        }

        if (preference.OptedOut)
        {
            return [];
        }

        var chosen = preference.Channels.Where(typeChannels.Contains).ToList();
        return chosen.Count > 0 ? chosen : [typeChannels[0]];
    }
}

/// <summary>A portal user's device for push notifications (§3, §8). The token is kept encrypted and looked up by its hash.</summary>
public sealed class DeviceRegistration : AggregateRoot<Guid>
{
    private DeviceRegistration()
    {
    }

    public Guid UserId { get; private set; }

    public Guid PartyId { get; private set; }

    public string Platform { get; private set; } = "";

    public string TokenHash { get; private set; } = "";

    public byte[] TokenEnc { get; private set; } = [];

    public DateTimeOffset RegisteredAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public static DeviceRegistration Register(Guid userId, Guid partyId, string platform, string tokenHash, byte[] tokenEnc, DateTimeOffset now)
    {
        var device = new DeviceRegistration
        {
            Id = Guid.CreateVersion7(),
            UserId = Guard.NotEmpty(userId),
            PartyId = Guard.NotEmpty(partyId),
            Platform = platform,
            TokenHash = tokenHash,
            TokenEnc = tokenEnc,
            RegisteredAt = now,
        };
        device.MarkChanged();
        return device;
    }

    public void Revoke(DateTimeOffset now)
    {
        RevokedAt ??= now;
        MarkChanged();
    }
}
