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
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.BuildingBlocks.Infrastructure.ProviderCallbacks;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Communications.Application;
using Insys.Communications.Domain;
using Insys.Communications.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Communications.Infrastructure.Persistence;

internal sealed class NotificationRepository(CommunicationsDbContext db) : INotificationRepository
{
    private IQueryable<Notification> WithAttempts => db.Notifications.Include(n => n.DeliveryAttempts);

    public Task<bool> Exists(string requestKey, CancellationToken cancellationToken) => db.Notifications.AnyAsync(n => n.RequestKey == requestKey, cancellationToken);

    public Task<Notification?> Get(Guid id, CancellationToken cancellationToken) => WithAttempts.SingleOrDefaultAsync(n => n.Id == id, cancellationToken);

    public async Task<Notification?> Lock(Guid id, CancellationToken cancellationToken)
    {
        // Held until the send is recorded: another instance skips it rather than send it twice.
        var locked = await db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM communication.notification WHERE tenant_id = {db.CurrentTenantId} AND id = {id} FOR UPDATE SKIP LOCKED
            """).ToListAsync(cancellationToken);
        return locked.Count == 0 ? null : await Get(id, cancellationToken);
    }

    public async Task<IReadOnlyList<Notification>> Due(DateTimeOffset now, int limit, CancellationToken cancellationToken) =>
        await db.Notifications.AsNoTracking().Where(n => n.Status == NotificationStatus.Queued && n.NextAttemptAt <= now).OrderBy(n => n.NextAttemptAt).ThenBy(n => n.Id)
            .Take(limit).ToListAsync(cancellationToken);

    public async Task<Notification?> ByProviderRef(string providerCode, string providerRef, CancellationToken cancellationToken) =>
        await db.DeliveryAttempts.AsNoTracking().Where(a => a.ProviderCode == providerCode && a.ProviderRef == providerRef).Select(a => (Guid?)a.NotificationId)
            .FirstOrDefaultAsync(cancellationToken) is { } id ? await Get(id, cancellationToken) : null;

    public async Task<IReadOnlyList<Notification>> QueuedMarketing(Guid partyId, string consentPurpose, CancellationToken cancellationToken) =>
        await WithAttempts.Where(n => n.RecipientPartyId == partyId && n.ConsentPurpose == consentPurpose && n.Status == NotificationStatus.Queued).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Notification>> ForParty(Guid partyId, CancellationToken cancellationToken) =>
        await WithAttempts.Where(n => n.RecipientPartyId == partyId).ToListAsync(cancellationToken);

    public void Add(Notification notification) => db.Notifications.Add(notification);

    public Task<NotificationPreference?> Preference(Guid partyId, string typeCode, CancellationToken cancellationToken) =>
        db.Preferences.SingleOrDefaultAsync(p => p.PartyId == partyId && p.TypeCode == typeCode, cancellationToken);

    public async Task<IReadOnlyList<NotificationPreference>> Preferences(Guid partyId, CancellationToken cancellationToken) =>
        await db.Preferences.AsNoTracking().Where(p => p.PartyId == partyId).OrderBy(p => p.TypeCode).ToListAsync(cancellationToken);

    public void Add(NotificationPreference preference) => db.Preferences.Add(preference);

    public Task<DeviceRegistration?> Device(string tokenHash, CancellationToken cancellationToken) =>
        db.Devices.SingleOrDefaultAsync(d => d.TokenHash == tokenHash, cancellationToken);

    public Task<DeviceRegistration?> Device(Guid id, CancellationToken cancellationToken) => db.Devices.SingleOrDefaultAsync(d => d.Id == id, cancellationToken);

    public Task<DeviceRegistration?> LatestDevice(Guid partyId, CancellationToken cancellationToken) =>
        db.Devices.AsNoTracking().Where(d => d.PartyId == partyId && d.RevokedAt == null).OrderByDescending(d => d.Id).FirstOrDefaultAsync(cancellationToken);

    public void Add(DeviceRegistration device) => db.Devices.Add(device);
}

/// <summary>Staff reads within the caller's F103 scope (the source record's product and node); member reads of their own party only (§8, §10).</summary>
internal sealed class NotificationQueries(CommunicationsDbContext db, ICommunicationCodes codes, IExecutionContext execution, IBusinessDate businessDate)
    : INotificationQueries
{
    private IQueryable<Notification> Scoped => db.Notifications.AsNoTracking().InScope(execution.CurrentScope, n => n.ProductCode, n => n.OrgNodeCode);

    public async Task<IReadOnlyList<Notification>> List(ListNotifications query, CancellationToken cancellationToken)
    {
        var rows = Scoped;
        if (query.PartyId is { } party)
        {
            rows = rows.Where(n => n.RecipientPartyId == party);
        }

        if (query.TypeCode is { } type)
        {
            rows = rows.Where(n => n.TypeCode == type);
        }

        if (query.Status is { } status)
        {
            rows = rows.Where(n => n.Status == status);
        }

        if (query.SourceType is { } sourceType)
        {
            rows = rows.Where(n => n.SourceType == sourceType && (query.SourceId == null || n.SourceId == query.SourceId));
        }

        if (query.AfterId is { } after)
        {
            rows = rows.Where(n => n.Id.CompareTo(after) > 0);
        }

        return await rows.OrderBy(n => n.Id).Take(query.Limit).ToListAsync(cancellationToken);
    }

    public Task<Notification?> InScope(Guid id, CancellationToken cancellationToken) =>
        Scoped.Include(n => n.DeliveryAttempts).SingleOrDefaultAsync(n => n.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Notification>> ForPortal(Guid partyId, Guid? cursorId, int limit, CancellationToken cancellationToken, Guid? onlyId = null)
    {
        var rows = db.Notifications.AsNoTracking().Where(n => n.RecipientPartyId == partyId && n.Status != NotificationStatus.SuppressedNoConsent);
        if (onlyId is { } only)
        {
            rows = rows.Where(n => n.Id == only);
        }

        if (cursorId is { } cursor)
        {
            rows = rows.Where(n => n.Id.CompareTo(cursor) < 0);
        }

        // Only types the tenant shows in the portal; the list is short, so it is read in pages and filtered.
        var visible = new Dictionary<string, bool>(StringComparer.Ordinal);
        var result = new List<Notification>();
        Guid? before = null;
        while (result.Count < limit)
        {
            var page = await (before is { } b ? rows.Where(n => n.Id.CompareTo(b) < 0) : rows).OrderByDescending(n => n.Id).Take(limit * 2).ToListAsync(cancellationToken);
            foreach (var notification in page)
            {
                if (!visible.TryGetValue(notification.TypeCode, out var shown))
                {
                    shown = (await codes.Type(notification.TypeCode, businessDate.Today, cancellationToken))?.PortalVisible == true;
                    visible[notification.TypeCode] = shown;
                }

                if (shown && result.Count < limit)
                {
                    result.Add(notification);
                }
            }

            if (page.Count < limit * 2)
            {
                break;
            }

            before = page[^1].Id;
        }

        return result;
    }
}

internal sealed class CommunicationCodes(CommunicationsDbContext db) : ICommunicationCodes
{
    public async Task<NotificationTypeDefinition?> Type(string typeCode, DateOnly day, CancellationToken cancellationToken) =>
        await InForce<NotificationTypes>(day).Where(t => t.TypeCode == typeCode).OrderByDescending(t => t.Version).FirstOrDefaultAsync(cancellationToken) is { } t
            ? new NotificationTypeDefinition(t.TypeCode, t.Category, t.ConsentPurpose, t.Channels, t.PortalVisible)
            : null;

    public async Task<TemplateDefinition?> Template(string typeCode, string channel, string language, DateOnly day, CancellationToken cancellationToken) =>
        await InForce<NotificationTemplates>(day).Where(t => t.TypeCode == typeCode && t.Channel == channel && t.Language == language).OrderByDescending(t => t.Version)
            .FirstOrDefaultAsync(cancellationToken) is { } t
            ? new TemplateDefinition(t.TypeCode, t.Channel, t.Language, t.Subject, t.Body)
            : null;

    public async Task<IReadOnlyList<ChannelProvider>> Providers(DateOnly day, CancellationToken cancellationToken) =>
        (await InForce<ChannelProviders>(day).Where(p => p.Enabled).OrderBy(p => p.Code).ThenByDescending(p => p.Version).ToListAsync(cancellationToken))
        .GroupBy(p => p.Code).Select(g => g.First())
        .Select(p => new ChannelProvider(p.Channel, p.ProviderCode, p.SenderIdentity)).ToList();

    private IQueryable<T> InForce<T>(DateOnly day) where T : StaticRecord =>
        db.Set<T>().AsNoTracking().InForce(CommunicationsStaticData.Descriptors.Single(d => d.EntityType == typeof(T)).Lifecycle, day);
}

internal sealed class CommunicationCallbackEndpoints(CommunicationsDbContext db) : CallbackEndpointStore(db), ICommunicationCallbackEndpoints;

internal sealed class CommunicationCallbackResolver([FromKeyedServices(CommunicationsDbContext.SchemaName)] Npgsql.NpgsqlDataSource dataSource)
    : ICommunicationCallbackResolver
{
    public Task<(Guid TenantId, Guid EndpointId)?> Resolve(string keyHash, CancellationToken cancellationToken) =>
        CallbackEndpointLookup.Resolve(dataSource, CommunicationsDbContext.SchemaName, keyHash, cancellationToken);
}

internal sealed class CommunicationsEventMapper : IDomainEventMapper
{
    public IIntegrationEvent? Map(IDomainEvent domainEvent) => domainEvent switch
    {
        NotificationWasSent e => new Contracts.NotificationSent(e.NotificationId, e.TypeCode, e.RecipientPartyId, e.SourceType, e.SourceId, e.Channel),
        NotificationWasDelivered e => new Contracts.NotificationDelivered(e.NotificationId, e.Channel),
        NotificationEnded e => new Contracts.NotificationFailed(e.NotificationId, e.TypeCode, e.RecipientPartyId, UpperCaseEnumConverter<NotificationStatus>.ToCode(e.Status)),
        MarketingOptedOut e => new Contracts.MarketingOptedOut(e.PartyId, e.ConsentPurpose, e.CaptureChannel),
        _ => null,
    };
}
