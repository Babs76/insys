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
using System.Text.Json;
using FluentValidation;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Application.ProviderCallbacks;
using Insys.BuildingBlocks.Application.Security;
using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Domain.ProviderCallbacks;
using Insys.Communications.Contracts;
using Insys.Communications.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Insys.Communications.Application;

// --- Views --------------------------------------------------------------------------------------------------------

public sealed record DeliveryAttemptView(short AttemptNo, string Channel, string? ProviderCode, string? ProviderRef, string? MaskedAddress,
    [property: OneOf("SENT", "DELIVERED", "FAILED", "BOUNCED", "UNDELIVERABLE")] string Status, string? FailureCode, DateTimeOffset AttemptedAt, DateTimeOffset StatusAt);

/// <summary>A notification for staff (§5): what was sent, on which channel, and every attempt with the address masked.</summary>
public sealed record NotificationView(Guid Id, string TypeCode, [property: OneOf("SERVICE", "MARKETING")] string Category, Guid RecipientPartyId, string SourceType,
    Guid SourceId, string? ProductCode, string? OrgNodeCode, IReadOnlyList<string> Channels, string Language, NotificationStatus Status, string? Channel, string? Subject,
    string? Body, DateTimeOffset? SentAt, DateTimeOffset? DeliveredAt, DateTimeOffset? EndedAt, IReadOnlyList<DeliveryAttemptView> Attempts, long RowVersion)
{
    public static NotificationView From(Notification n) => new(n.Id, n.TypeCode, n.Category, n.RecipientPartyId, n.SourceType, n.SourceId, n.ProductCode,
        n.OrgNodeCode, n.Channels, n.Language, n.Status, n.Channel, n.Subject, n.Body, n.SentAt, n.DeliveredAt, n.EndedAt,
        n.DeliveryAttempts.OrderBy(a => a.AttemptNo).Select(a => new DeliveryAttemptView(a.AttemptNo, a.Channel, a.ProviderCode, a.ProviderRef, a.MaskedAddress,
            Upper(a.Status), a.FailureCode, a.AttemptedAt, a.StatusAt)).ToList(), n.RowVersion);

    internal static string Upper(AttemptStatus status) => status.ToString().ToUpperInvariant();
}

/// <summary>A notification in a list: without its text.</summary>
public sealed record NotificationSummary(Guid Id, string TypeCode, Guid RecipientPartyId, string SourceType, Guid SourceId, NotificationStatus Status, string? Channel,
    DateTimeOffset? SentAt, DateTimeOffset? EndedAt);

/// <summary>A notification in the member portal (§8): what was sent, the channel and the status; never the address.</summary>
public sealed record PortalNotificationView(Guid Id, string TypeCode, NotificationStatus Status, string? Channel, string? Subject, string? Body, DateTimeOffset? SentAt,
    DateTimeOffset? DeliveredAt)
{
    public static PortalNotificationView From(Notification n) => new(n.Id, n.TypeCode, n.Status, n.Channel, n.Subject, n.Body, n.SentAt, n.DeliveredAt);
}

public sealed record PreferenceView(string TypeCode, IReadOnlyList<string> Channels, bool OptedOut, long RowVersion)
{
    public static PreferenceView From(NotificationPreference p) => new(p.TypeCode, p.Channels, p.OptedOut, p.RowVersion);
}

public sealed record DeviceView(Guid Id, [property: OneOf("IOS", "ANDROID", "WEB")] string Platform, DateTimeOffset RegisteredAt, DateTimeOffset? RevokedAt);

public sealed record CommunicationCallbackEndpointView(Guid Id, string ProviderCode, CallbackEndpointState Status, bool RequireClientToken, DateTimeOffset IssuedAt,
    string IssuedBy, DateTimeOffset? RetiresAt, DateTimeOffset? RevokedAt, long RowVersion)
{
    public static CommunicationCallbackEndpointView From(CallbackEndpoint e) =>
        new(e.Id, e.ProviderCode, e.Status, e.RequireClientToken, e.IssuedAt, e.IssuedBy, e.RetiresAt, e.RevokedAt, e.RowVersion);
}

/// <summary>A new endpoint: the key and the signing secret are shown here once, and never again.</summary>
public sealed record IssuedCommunicationCallbackEndpoint(CommunicationCallbackEndpointView Endpoint, string CallbackPath, string EndpointKey, string SigningSecret);

// --- Commands and queries -----------------------------------------------------------------------------------------

/// <summary>Staff list notifications by party, type, status or source, within their F103 scope; by ID with cursor paging.</summary>
public sealed record ListNotifications(Guid? PartyId, string? TypeCode, NotificationStatus? Status, string? SourceType, Guid? SourceId, Guid? AfterId, int Limit)
    : IQuery<IReadOnlyList<NotificationSummary>>;

public sealed record GetNotification(Guid NotificationId) : IQuery<NotificationView>;

public sealed record ListPortalNotifications(Guid? AfterId, int Limit) : IQuery<IReadOnlyList<PortalNotificationView>>;

public sealed record GetPortalNotification(Guid NotificationId) : IQuery<PortalNotificationView>;

public sealed record GetPreferences(Guid PartyId) : IQuery<IReadOnlyList<PreferenceView>>;

public sealed record GetMyPreferences : IQuery<IReadOnlyList<PreferenceView>>;

/// <summary>Set a party's channels for a type, in order, or opt out of a marketing type (§4).</summary>
public sealed record SetPreference(Guid PartyId, string TypeCode, IReadOnlyList<string> Channels, bool OptedOut) : ICommand<PreferenceView>;

/// <summary>A member sets their own preference in the portal.</summary>
public sealed record SetMyPreference(string TypeCode, IReadOnlyList<string> Channels, bool OptedOut) : ICommand<PreferenceView>;

/// <summary>A portal app registers the signed-in member's device for push (§3, §8); the same token again is the same device.</summary>
public sealed record RegisterDevice(string Platform, string Token) : ICommand<DeviceView>;

public sealed record RevokeDevice(Guid DeviceId) : ICommand<DeviceView>;

public sealed record IssueCommunicationCallbackEndpoint(string ProviderCode, bool RequireClientToken) : ICommand<IssuedCommunicationCallbackEndpoint>;

public sealed record RevokeCommunicationCallbackEndpoint(Guid EndpointId) : ICommand<CommunicationCallbackEndpointView>;

public sealed record ListCommunicationCallbackEndpoints : IQuery<IReadOnlyList<CommunicationCallbackEndpointView>>;

public enum DeliveryCallbackResult
{
    Applied,
    Unauthorized,
    Replayed,
    Invalid,
    Unknown,
}

/// <summary>A provider's delivery report at its endpoint's address, in the endpoint's tenant (set from the key) (§5.1).</summary>
public sealed record ReceiveDeliveryCallback(Guid EndpointId, string Body, string? Timestamp, string? CallbackId, string? Signature, bool ClientTokenValid)
    : ICommand<DeliveryCallbackResult>;

public sealed class CommunicationCallbackOptions : CallbackOptions;

/// <summary>Communications' callback endpoints (communication.callback_endpoint and communication.callback_receipt).</summary>
public interface ICommunicationCallbackEndpoints : ICallbackEndpointStore;

/// <summary>Finds the tenant and endpoint for an endpoint key's hash, across tenants (communication.resolve_callback_endpoint, V0051).</summary>
public interface ICommunicationCallbackResolver
{
    Task<(Guid TenantId, Guid EndpointId)?> Resolve(string keyHash, CancellationToken cancellationToken);
}

/// <summary>The F103-scoped and portal reads of notifications.</summary>
public interface INotificationQueries
{
    Task<IReadOnlyList<Notification>> List(ListNotifications query, CancellationToken cancellationToken);

    /// <summary>A notification within the caller's F103 scope; null outside it.</summary>
    Task<Notification?> InScope(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// The party's notifications of portal-visible types, newest first, older than the cursor; or only <paramref name="onlyId"/>
    /// when it is the party's and portal-visible.
    /// </summary>
    Task<IReadOnlyList<Notification>> ForPortal(Guid partyId, Guid? cursorId, int limit, CancellationToken cancellationToken, Guid? onlyId = null);
}

// --- Validators ---------------------------------------------------------------------------------------------------

public sealed class SetPreferenceValidator : AbstractValidator<SetPreference>
{
    public SetPreferenceValidator()
    {
        RuleFor(c => c.PartyId).NotEmpty();
        RuleFor(c => c.TypeCode).NotEmpty().MaximumLength(30);
        RuleForEach(c => c.Channels).Must(Contracts.Channels.All.Contains).WithMessage("A channel is EMAIL, SMS, PUSH or WHATSAPP.");
    }
}

public sealed class SetMyPreferenceValidator : AbstractValidator<SetMyPreference>
{
    public SetMyPreferenceValidator()
    {
        RuleFor(c => c.TypeCode).NotEmpty().MaximumLength(30);
        RuleForEach(c => c.Channels).Must(Contracts.Channels.All.Contains).WithMessage("A channel is EMAIL, SMS, PUSH or WHATSAPP.");
    }
}

public sealed class RegisterDeviceValidator : AbstractValidator<RegisterDevice>
{
    public RegisterDeviceValidator()
    {
        RuleFor(c => c.Platform).Must(p => p is "IOS" or "ANDROID" or "WEB").WithMessage("platform is IOS, ANDROID or WEB.");
        RuleFor(c => c.Token).NotEmpty().MaximumLength(4096);
    }
}

public sealed class IssueCommunicationCallbackEndpointValidator : AbstractValidator<IssueCommunicationCallbackEndpoint>
{
    public IssueCommunicationCallbackEndpointValidator() => RuleFor(c => c.ProviderCode).NotEmpty().MaximumLength(20);
}

public sealed class ListNotificationsValidator : AbstractValidator<ListNotifications>
{
    public ListNotificationsValidator()
    {
        RuleFor(q => q.Limit).InclusiveBetween(1, 200);
        RuleFor(q => q.SourceType).MaximumLength(30);
        RuleFor(q => q).Must(q => q.SourceId is null || q.SourceType is not null).WithMessage("sourceId needs sourceType.");
    }
}

// --- Handlers -----------------------------------------------------------------------------------------------------

internal sealed class NotificationQueryHandlers(INotificationQueries queries, IExecutionContext execution) :
    IQueryHandler<ListNotifications, IReadOnlyList<NotificationSummary>>,
    IQueryHandler<GetNotification, NotificationView>,
    IQueryHandler<ListPortalNotifications, IReadOnlyList<PortalNotificationView>>,
    IQueryHandler<GetPortalNotification, PortalNotificationView>
{
    public async Task<Result<IReadOnlyList<NotificationSummary>>> Handle(ListNotifications query, CancellationToken cancellationToken) =>
        Result.Success<IReadOnlyList<NotificationSummary>>((await queries.List(query, cancellationToken))
            .Select(n => new NotificationSummary(n.Id, n.TypeCode, n.RecipientPartyId, n.SourceType, n.SourceId, n.Status, n.Channel, n.SentAt, n.EndedAt)).ToList());

    public async Task<Result<NotificationView>> Handle(GetNotification query, CancellationToken cancellationToken) =>
        await queries.InScope(query.NotificationId, cancellationToken) is { } notification
            ? NotificationView.From(notification)
            : Error.NotFound(ErrorCodes.NotFound, "The notification was not found.");

    public async Task<Result<IReadOnlyList<PortalNotificationView>>> Handle(ListPortalNotifications query, CancellationToken cancellationToken) =>
        Member() is { } party
            ? Result.Success<IReadOnlyList<PortalNotificationView>>((await queries.ForPortal(party, query.AfterId, query.Limit, cancellationToken))
                .Select(PortalNotificationView.From).ToList())
            : Result.Success<IReadOnlyList<PortalNotificationView>>([]);

    /// <summary>Only a notification addressed to the member's own party, of a portal-visible type; another's is not found (§8).</summary>
    public async Task<Result<PortalNotificationView>> Handle(GetPortalNotification query, CancellationToken cancellationToken)
    {
        if (Member() is { } party)
        {
            var mine = await queries.ForPortal(party, null, 1, cancellationToken, query.NotificationId);
            if (mine.Count == 1)
            {
                return PortalNotificationView.From(mine[0]);
            }
        }

        return Error.NotFound(ErrorCodes.NotFound, "The notification was not found.");
    }

    private Guid? Member() => execution.External is { Kind: ExternalSubject.Party, PartyId: { } party } ? party : null;
}

internal sealed class PreferenceHandlers(INotificationRepository notifications, ICommunicationCodes codes, IExecutionContext execution, IBusinessDate businessDate,
    IClock clock, IAuditTrail audit) :
    IQueryHandler<GetPreferences, IReadOnlyList<PreferenceView>>,
    IQueryHandler<GetMyPreferences, IReadOnlyList<PreferenceView>>,
    ICommandHandler<SetPreference, PreferenceView>,
    ICommandHandler<SetMyPreference, PreferenceView>
{
    public async Task<Result<IReadOnlyList<PreferenceView>>> Handle(GetPreferences query, CancellationToken cancellationToken) =>
        Result.Success<IReadOnlyList<PreferenceView>>((await notifications.Preferences(query.PartyId, cancellationToken)).Select(PreferenceView.From).ToList());

    public async Task<Result<IReadOnlyList<PreferenceView>>> Handle(GetMyPreferences query, CancellationToken cancellationToken) =>
        execution.External is { Kind: ExternalSubject.Party, PartyId: { } party }
            ? Result.Success<IReadOnlyList<PreferenceView>>((await notifications.Preferences(party, cancellationToken)).Select(PreferenceView.From).ToList())
            : Result.Success<IReadOnlyList<PreferenceView>>([]);

    public Task<Result<PreferenceView>> Handle(SetPreference command, CancellationToken cancellationToken) =>
        Set(command.PartyId, command.TypeCode, command.Channels, command.OptedOut, "STAFF", cancellationToken);

    public Task<Result<PreferenceView>> Handle(SetMyPreference command, CancellationToken cancellationToken) =>
        execution.External is { Kind: ExternalSubject.Party, PartyId: { } party }
            ? Set(party, command.TypeCode, command.Channels, command.OptedOut, "PORTAL", cancellationToken)
            : Task.FromResult<Result<PreferenceView>>(Error.Forbidden(ErrorCodes.Forbidden, "Preferences are a member's own."));

    private async Task<Result<PreferenceView>> Set(Guid partyId, string typeCode, IReadOnlyList<string> channels, bool optedOut, string captureChannel,
        CancellationToken cancellationToken)
    {
        if (await codes.Type(typeCode, businessDate.Today, cancellationToken) is not { } type)
        {
            return Error.Validation(CommunicationErrorCodes.PreferenceNotAllowed, $"{typeCode} is not a notification type in force.");
        }

        var current = await notifications.Preference(partyId, typeCode, cancellationToken);
        var set = NotificationPreference.Set(current, partyId, typeCode, type.IsMarketing ? type.ConsentPurpose : null, type.Channels, channels, optedOut, captureChannel,
            clock.UtcNow);
        if (set.IsFailure)
        {
            return set.Error;
        }

        if (current is null)
        {
            notifications.Add(set.Value);
        }

        audit.Record(new AuditEntry("COMMUNICATION", "SetPreference", nameof(NotificationPreference), set.Value.Id.ToString(),
            new Dictionary<string, string?> { ["type"] = typeCode, ["optedOut"] = optedOut ? "true" : "false", ["by"] = captureChannel }));
        return PreferenceView.From(set.Value);
    }
}

internal sealed class DeviceHandlers(INotificationRepository notifications, IFieldProtector protector, IExecutionContext execution, IClock clock) :
    ICommandHandler<RegisterDevice, DeviceView>,
    ICommandHandler<RevokeDevice, DeviceView>
{
    public async Task<Result<DeviceView>> Handle(RegisterDevice command, CancellationToken cancellationToken)
    {
        if (execution.External is not { Kind: ExternalSubject.Party, PartyId: { } party } || !Guid.TryParse(execution.ActorId, out var user))
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Devices are registered by a member for themselves.");
        }

        var hash = protector.Hash(command.Token);
        if (await notifications.Device(hash, cancellationToken) is { RevokedAt: null } existing && existing.UserId == user)
        {
            return View(existing);
        }

        var device = DeviceRegistration.Register(user, party, command.Platform, hash, protector.Protect(command.Token).Encrypted, clock.UtcNow);
        notifications.Add(device);
        return View(device);
    }

    public async Task<Result<DeviceView>> Handle(RevokeDevice command, CancellationToken cancellationToken)
    {
        if (await notifications.Device(command.DeviceId, cancellationToken) is not { } device || device.UserId.ToString() != execution.ActorId)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The device was not found.");
        }

        device.Revoke(clock.UtcNow);
        return View(device);
    }

    private static DeviceView View(DeviceRegistration d) => new(d.Id, d.Platform, d.RegisteredAt, d.RevokedAt);
}

internal sealed class CommunicationCallbackEndpointHandlers(ICommunicationCallbackEndpoints endpoints, IFieldProtector protector, IExecutionContext execution,
    IClock clock, IOptions<CommunicationCallbackOptions> options, IAuditTrail audit) :
    ICommandHandler<IssueCommunicationCallbackEndpoint, IssuedCommunicationCallbackEndpoint>,
    ICommandHandler<RevokeCommunicationCallbackEndpoint, CommunicationCallbackEndpointView>,
    IQueryHandler<ListCommunicationCallbackEndpoints, IReadOnlyList<CommunicationCallbackEndpointView>>
{
    public async Task<Result<IssuedCommunicationCallbackEndpoint>> Handle(IssueCommunicationCallbackEndpoint command, CancellationToken cancellationToken)
    {
        var issued = await CallbackEndpoints.Issue(endpoints, protector, command.ProviderCode, command.RequireClientToken, execution.ActorId, clock.UtcNow,
            options.Value, cancellationToken);

        // Never the key or the secret: the audit names the endpoint and provider only.
        audit.Record(new AuditEntry("COMMUNICATION", "IssueCallbackEndpoint", nameof(CallbackEndpoint), issued.Endpoint.Id.ToString(),
            new Dictionary<string, string?> { ["provider"] = command.ProviderCode, ["requireClientToken"] = command.RequireClientToken ? "true" : "false" }));
        return new IssuedCommunicationCallbackEndpoint(CommunicationCallbackEndpointView.From(issued.Endpoint),
            $"/api/v1/communications/callbacks/{issued.EndpointKey}", issued.EndpointKey, issued.SigningSecret);
    }

    public async Task<Result<CommunicationCallbackEndpointView>> Handle(RevokeCommunicationCallbackEndpoint command, CancellationToken cancellationToken)
    {
        var revoked = await CallbackEndpoints.Revoke(endpoints, command.EndpointId, execution.ActorId, clock.UtcNow, cancellationToken);
        if (revoked.IsFailure)
        {
            return revoked.Error;
        }

        audit.Record(new AuditEntry("COMMUNICATION", "RevokeCallbackEndpoint", nameof(CallbackEndpoint), revoked.Value.Id.ToString(),
            new Dictionary<string, string?> { ["provider"] = revoked.Value.ProviderCode }));
        return CommunicationCallbackEndpointView.From(revoked.Value);
    }

    public async Task<Result<IReadOnlyList<CommunicationCallbackEndpointView>>> Handle(ListCommunicationCallbackEndpoints query, CancellationToken cancellationToken) =>
        Result.Success<IReadOnlyList<CommunicationCallbackEndpointView>>((await endpoints.All(cancellationToken)).Select(CommunicationCallbackEndpointView.From).ToList());
}

/// <summary>
/// A provider's delivery report (§5.1): body {"providerRef": "...", "status": "DELIVERED" | "BOUNCED" | "FAILED"}. Signed
/// as ADR-023 requires; an unknown reference is acknowledged, so the provider does not retry forever; a repeat is a no-op.
/// </summary>
internal sealed class ReceiveDeliveryCallbackHandler(ICommunicationCallbackEndpoints endpoints, INotificationRepository notifications, IFieldProtector protector,
    IClock clock, IOptions<CommunicationCallbackOptions> options, ILogger<ReceiveDeliveryCallbackHandler> logger)
    : ICommandHandler<ReceiveDeliveryCallback, DeliveryCallbackResult>
{
    public async Task<Result<DeliveryCallbackResult>> Handle(ReceiveDeliveryCallback command, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        if (await endpoints.Get(command.EndpointId, cancellationToken) is not { } endpoint || !endpoint.UsableAt(now))
        {
            return DeliveryCallbackResult.Unauthorized;
        }

        var signed = CallbackEndpoints.Signed(endpoint, protector, command.Body, command.Timestamp, command.CallbackId, command.Signature, now, options.Value);
        if (!signed || (endpoint.RequireClientToken && !command.ClientTokenValid))
        {
            logger.LogWarning("Delivery callback refused at endpoint {EndpointId} ({Provider})", endpoint.Id, endpoint.ProviderCode);
            return DeliveryCallbackResult.Unauthorized;
        }

        if (await CallbackEndpoints.Admit(endpoints, endpoint, command.CallbackId!, now, options.Value, cancellationToken) == CallbackAuthentication.Replayed)
        {
            return DeliveryCallbackResult.Replayed;
        }

        if (Parse(command.Body) is not var (providerRef, outcome))
        {
            return DeliveryCallbackResult.Invalid;
        }

        if (await notifications.ByProviderRef(endpoint.ProviderCode, providerRef, cancellationToken) is not { } notification
            || !notification.Confirm(providerRef, outcome, now))
        {
            logger.LogInformation("Delivery callback at endpoint {EndpointId} for an unknown provider reference", endpoint.Id);
            return DeliveryCallbackResult.Unknown;
        }

        return DeliveryCallbackResult.Applied;
    }

    private static (string ProviderRef, DeliveryOutcome Outcome)? Parse(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("providerRef", out var reference) || reference.GetString() is not { Length: > 0 and <= 120 } providerRef
                || !root.TryGetProperty("status", out var status))
            {
                return null;
            }

            return status.GetString() switch
            {
                "DELIVERED" => (providerRef, DeliveryOutcome.Delivered),
                "BOUNCED" => (providerRef, DeliveryOutcome.Bounced),
                "FAILED" => (providerRef, DeliveryOutcome.Failed),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
