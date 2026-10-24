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
using Insys.BuildingBlocks.Application.Security;
using Insys.BuildingBlocks.Domain;
using Insys.Communications.Application.Channels;
using Insys.Communications.Contracts;
using Insys.Communications.Domain;
using Insys.Party.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Insys.Communications.Application;

/// <summary>The queued notifications due now, by ID, for the sending job to send one at a time.</summary>
public sealed record FindDueNotifications(int Limit) : IQuery<IReadOnlyList<Guid>>;

/// <summary>Sends one due notification on its next channel (§3, §5). Its row is locked for the send, so no two instances send it.</summary>
public sealed record SendNotification(Guid NotificationId) : ICommand<NotificationStatus?>;

internal sealed class FindDueNotificationsHandler(INotificationRepository notifications, IClock clock) : IQueryHandler<FindDueNotifications, IReadOnlyList<Guid>>
{
    public async Task<Result<IReadOnlyList<Guid>>> Handle(FindDueNotifications query, CancellationToken cancellationToken) =>
        Result.Success<IReadOnlyList<Guid>>((await notifications.Due(clock.UtcNow, query.Limit, cancellationToken)).Select(n => n.Id).ToList());
}

/// <summary>
/// One send: marketing is checked for consent again now (§4); the address is read from Party (or the push device) and
/// never stored, only masked; the template is the channel's in the notification's language, else the chain's; sender
/// identity, portal link and opt-out contact are added to the merge values. No address uses the channel up; a refusal
/// is retried or moves on, as the notification decides.
/// </summary>
internal sealed class SendNotificationHandler(INotificationRepository notifications, ICommunicationCodes codes, INotificationChannels channels, IPartyContacts contacts,
    IPartyConsent consent, IPartyLanguage languages, ILanguageSettings settings, IFieldProtector protector, ITenantContext tenant, IBusinessDate businessDate,
    IClock clock, IOptions<CommunicationOptions> options, ILogger<SendNotificationHandler> logger) : ICommandHandler<SendNotification, NotificationStatus?>
{
    public async Task<Result<NotificationStatus?>> Handle(SendNotification command, CancellationToken cancellationToken)
    {
        if (await notifications.Lock(command.NotificationId, cancellationToken) is not { Status: NotificationStatus.Queued } notification)
        {
            return (NotificationStatus?)null;
        }

        var tenantId = tenant.RequireTenantId();
        var now = clock.UtcNow;
        if (notification.IsMarketing && !await consent.IsGranted(tenantId, notification.RecipientPartyId, notification.ConsentPurpose!, cancellationToken))
        {
            notification.SuppressForConsent(now);
            return notification.Status;
        }

        if (notification.NextChannel() is not { } channelCode)
        {
            return notification.Status;
        }

        var day = businessDate.Today;
        var provider = (await codes.Providers(day, cancellationToken)).FirstOrDefault(p => p.Channel == channelCode);
        var channel = channels.For(channelCode);
        if (provider is null || channel is null)
        {
            notification.Refused(channelCode, provider?.ProviderCode ?? "NONE", "", "PROVIDER_NOT_CONFIGURED", retryable: false, now);
            return notification.Status;
        }

        if (await Address(notification, channelCode, tenantId, cancellationToken) is not { } address)
        {
            notification.NoAddress(channelCode, now);
            return notification.Status;
        }

        var masked = Addresses.Mask(channelCode, address);
        if (await Template(notification, channelCode, tenantId, day, cancellationToken) is not { } template)
        {
            notification.Refused(channelCode, provider.ProviderCode, masked, "TEMPLATE_NOT_IN_FORCE", retryable: false, now);
            return notification.Status;
        }

        var values = new Dictionary<string, string>(notification.MergeValues, StringComparer.Ordinal) { [MergeFields.SenderIdentity] = provider.SenderIdentity };
        if (options.Value.PortalLink is { Length: > 0 } link)
        {
            values[MergeFields.PortalLink] = link;
        }

        if (options.Value.OptOutContact is { Length: > 0 } optOut)
        {
            values[MergeFields.OptOutContact] = optOut;
        }

        var subject = template.Subject is null ? null : MergeFields.Render(template.Subject, values);
        var body = MergeFields.Render(template.Body, values);
        var receipt = await channel.Send(new OutboundMessage(notification.Id, tenantId, provider.ProviderCode, provider.SenderIdentity, address, subject, body), cancellationToken);
        if (receipt.Accepted)
        {
            notification.Accepted(channelCode, provider.ProviderCode, receipt.ProviderRef!, masked, subject, body, now);
        }
        else
        {
            logger.LogInformation("Notification {NotificationId} refused on {Channel}: {FailureCode}", notification.Id, channelCode, receipt.FailureCode);
            notification.Refused(channelCode, provider.ProviderCode, masked, receipt.FailureCode ?? ChannelFailures.MessageRejected, receipt.Retryable, now);
        }

        return notification.Status;
    }

    private async Task<string?> Address(Notification notification, string channel, Guid tenantId, CancellationToken cancellationToken)
    {
        if (channel == Contracts.Channels.Push)
        {
            return await notifications.LatestDevice(notification.RecipientPartyId, cancellationToken) is { } device ? protector.Unprotect(device.TokenEnc) : null;
        }

        return Addresses.ContactType(channel) is { } type ? await contacts.Primary(tenantId, notification.RecipientPartyId, type, cancellationToken) : null;
    }

    private async Task<TemplateDefinition?> Template(Notification notification, string channel, Guid tenantId, DateOnly day, CancellationToken cancellationToken)
    {
        var chain = LanguageChain.Chain(await languages.PreferredLanguage(tenantId, notification.RecipientPartyId, cancellationToken),
            await settings.For(tenantId, cancellationToken));
        foreach (var language in new[] { notification.Language }.Concat(chain).Distinct(StringComparer.Ordinal))
        {
            if (await codes.Template(notification.TypeCode, channel, language, day, cancellationToken) is { } template)
            {
                return template;
            }
        }

        return null;
    }
}
