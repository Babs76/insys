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
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Localisation;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.Communications.Contracts;
using Insys.Communications.Domain;
using Insys.Party.Contracts;

namespace Insys.Communications.Application;

/// <summary>A notification to queue: what a trigger asks for (communications §1, §6).</summary>
public sealed record NotificationRequest(string TypeCode, Guid RecipientPartyId, string SourceType, Guid SourceId, IReadOnlyDictionary<string, string> MergeValues,
    string? ProductCode = null, string? OrgNodeCode = null);

/// <summary>
/// Queues one notification for one trigger (addenda/communications.md §2–§5, §9). In order:
/// - one per event, type and recipient: a redelivered trigger queues nothing (INT-005);
/// - the type must be configured, or the trigger parks (NOTIFICATION_TYPE_NOT_CONFIGURED);
/// - marketing the party opted out of, or has not consented to, is recorded as SUPPRESSED_NO_CONSENT and never sent (§4);
/// - the channels are the party's order within the type's, kept to those with an enabled provider (or it parks:
///   CHANNEL_PROVIDER_NOT_CONFIGURED) and a template in a language of the chain (or it parks:
///   NOTIFICATION_TEMPLATE_NOT_IN_FORCE): the party's preferred language, the tenant default, then English.
/// The sending job sends it; nothing is sent here.
/// </summary>
public sealed class NotificationQueue(INotificationRepository notifications, ICommunicationCodes codes, IPartyLanguage languages, ILanguageSettings settings,
    IPartyConsent consent, ITenantContext tenant, IBusinessDate businessDate, IClock clock)
{
    public async Task<Notification?> Queue(NotificationRequest request, Guid eventId, CancellationToken cancellationToken)
    {
        var requestKey = $"{eventId:N}:{request.TypeCode}:{request.RecipientPartyId:N}";
        if (await notifications.Exists(requestKey, cancellationToken))
        {
            return null;
        }

        var tenantId = tenant.RequireTenantId();
        var day = businessDate.Today;
        var now = clock.UtcNow;
        var type = await codes.Type(request.TypeCode, day, cancellationToken)
                   ?? throw new PrerequisiteMissingException(PrerequisiteCodes.NotificationTypeNotConfigured, request.TypeCode);
        var order = NotificationPreference.Order(await notifications.Preference(request.RecipientPartyId, type.TypeCode, cancellationToken), type.Channels);
        var chain = LanguageChain.Chain(await languages.PreferredLanguage(tenantId, request.RecipientPartyId, cancellationToken),
            await settings.For(tenantId, cancellationToken));

        if (type.IsMarketing && (order.Count == 0 || !await consent.IsGranted(tenantId, request.RecipientPartyId, type.ConsentPurpose!, cancellationToken)))
        {
            var suppressed = Create(requestKey, type, request, type.Channels, chain[0], now);
            suppressed.SuppressForConsent(now);
            return suppressed;
        }

        var enabled = (await codes.Providers(day, cancellationToken)).Select(p => p.Channel).ToHashSet(StringComparer.Ordinal);
        var withProvider = order.Where(enabled.Contains).ToList();
        if (withProvider.Count == 0)
        {
            throw new PrerequisiteMissingException(PrerequisiteCodes.ChannelProviderNotConfigured, Item(type.TypeCode, order));
        }

        var channels = new List<string>();
        string? language = null;
        foreach (var channel in withProvider)
        {
            foreach (var candidate in chain)
            {
                if (await codes.Template(type.TypeCode, channel, candidate, day, cancellationToken) is not null)
                {
                    channels.Add(channel);
                    language ??= candidate;
                    break;
                }
            }
        }

        if (channels.Count == 0)
        {
            throw new PrerequisiteMissingException(PrerequisiteCodes.NotificationTemplateNotInForce, Item(type.TypeCode, withProvider));
        }

        return Create(requestKey, type, request, channels, language!, now);
    }

    private Notification Create(string requestKey, NotificationTypeDefinition type, NotificationRequest request, IReadOnlyList<string> channels, string language,
        DateTimeOffset now)
    {
        var notification = Notification.Queue(Guid.CreateVersion7(), requestKey, type.TypeCode, type.Category, type.IsMarketing ? type.ConsentPurpose : null,
            request.RecipientPartyId, request.SourceType, request.SourceId, request.ProductCode, request.OrgNodeCode, request.MergeValues, channels, language, now);
        notifications.Add(notification);
        return notification;
    }

    private static string Item(string typeCode, IEnumerable<string> channels) => $"{typeCode}:{string.Join(',', channels)}";
}

/// <summary>A module asked for a notification (communication.NotificationRequested, §1).</summary>
public sealed class QueueRequestedNotification(NotificationQueue queue) : IIntegrationEventHandler<NotificationRequested>
{
    public Task Handle(NotificationRequested requested, EventEnvelope envelope, CancellationToken cancellationToken) =>
        queue.Queue(new NotificationRequest(requested.NotificationType, requested.RecipientPartyId, requested.SourceType, requested.SourceId, requested.MergeValues,
            requested.ProductCode, requested.OrgNodeCode), envelope.EventId, cancellationToken);
}

/// <summary>Consent withdrawn: the party's queued marketing for that purpose is suppressed at once (§4).</summary>
public sealed class SuppressMarketingOnWithdrawal(INotificationRepository notifications, IClock clock) : IIntegrationEventHandler<ConsentWithdrawn>
{
    public async Task Handle(ConsentWithdrawn withdrawn, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        foreach (var notification in await notifications.QueuedMarketing(withdrawn.PartyId, withdrawn.PurposeCode, cancellationToken))
        {
            notification.SuppressForConsent(clock.UtcNow);
        }
    }
}

/// <summary>The party is anonymised (POPIA): what was sent to them is blanked (§10).</summary>
public sealed class AnonymiseNotifications(INotificationRepository notifications) : IIntegrationEventHandler<PartyAnonymised>
{
    public async Task Handle(PartyAnonymised anonymised, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        foreach (var notification in await notifications.ForParty(anonymised.PartyId, cancellationToken))
        {
            notification.Anonymise();
        }
    }
}
