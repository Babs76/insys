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
using Insys.BuildingBlocks.Api;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Application.ProviderCallbacks;
using Insys.BuildingBlocks.Domain;
using Insys.Communications.Api.StaticData;
using Insys.Communications.Application;
using Insys.Communications.Contracts;
using Insys.Communications.Domain;
using Insys.Communications.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Communications.Api;

public sealed record PreferenceRequest(IReadOnlyList<string>? Channels, bool OptedOut);

public sealed record DeviceRequest([property: OneOf("IOS", "ANDROID", "WEB")] string Platform, string Token);

public sealed record CommunicationCallbackEndpointRequest(string ProviderCode, bool RequireClientToken);

/// <summary>A provider's delivery report (§5.1).</summary>
public sealed record DeliveryCallbackRequest(string ProviderRef, [property: OneOf("DELIVERED", "BOUNCED", "FAILED")] string Status);

/// <summary>
/// The Communications module (addenda/communications.md, Wave 1: F021, F031, F078, F095–F102): notification types and
/// templates, channels behind adapters, preferences and consent, delivery tracking and provider callbacks.
/// </summary>
public sealed class CommunicationsModule : IModule
{
    public string Name => "Communications";

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddCommunicationsInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        CommunicationsStaticDataEndpoints.MapStaticData(api);
        var communications = api.MapGroup("/communications").WithTags(Name);

        communications.MapGet("/notifications", async (Guid? partyId, string? typeCode, NotificationStatus? status, string? sourceType, Guid? sourceId, string? cursor,
                int? limit, IDispatcher d, CancellationToken ct) =>
                await Page(cursor, limit, (after, size) => d.Query(new ListNotifications(partyId, typeCode, status, sourceType, sourceId, after, size), ct), n => n.Id))
            .Produces<CursorPage<NotificationSummary>>(StatusCodes.Status200OK)
            .WithName("CommunicationsListNotifications")
            .WithSummary("Notifications, by recipient party, type, status or source record, oldest first (§5)")
            .WithDescription("Within the caller's scope (F103): a notification is in scope when the product and organisation node of the record it is about are.")
            .RequirePermission(CommunicationPermissions.NotificationRead);

        communications.MapGet("/notifications/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetNotification(id), ct)).ToHttp(Results.Ok))
            .Produces<NotificationView>(StatusCodes.Status200OK)
            .WithName("CommunicationsGetNotification")
            .WithSummary("A notification: the text as sent, and every delivery attempt with the address masked (§5)")
            .RequirePermission(CommunicationPermissions.NotificationRead);

        communications.MapGet("/parties/{partyId:guid}/preferences", async (Guid partyId, IDispatcher d, CancellationToken ct) =>
                (await d.Query(new GetPreferences(partyId), ct)).ToHttp(Results.Ok))
            .Produces<IReadOnlyList<PreferenceView>>(StatusCodes.Status200OK)
            .WithName("CommunicationsGetPreferences")
            .WithSummary("A party's channel preferences per notification type (§4); a type with none uses its default order")
            .RequirePermission(CommunicationPermissions.PreferenceRead);

        communications.MapPut("/parties/{partyId:guid}/preferences/{typeCode}", async (Guid partyId, string typeCode, PreferenceRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SetPreference(partyId, typeCode, r.Channels ?? [], r.OptedOut), ct)).ToHttp(Results.Ok))
            .Produces<PreferenceView>(StatusCodes.Status200OK)
            .WithName("CommunicationsSetPreference")
            .WithSummary("Set a party's channels for a type, in order, or opt out of a marketing type (§4)")
            .WithDescription("A whole replacement, so repeating it changes nothing. A service notice keeps at least one channel (PREFERENCE_NOT_ALLOWED to opt out). "
                + "Opting out of marketing also records the withdrawal of its consent purpose in Party.")
            .WithEventualEffects("an opt-out reaches the party's consent ledger as a withdrawal (party consents); read the consents again to see it.")
            .RequirePermission(CommunicationPermissions.PreferenceUpdate);

        MapCallbacks(communications);
        MapPortal(api);
    }

    /// <summary>ADR-023: delivery callback addresses per tenant and provider, and the address providers call (§5.1).</summary>
    private static void MapCallbacks(RouteGroupBuilder communications)
    {
        var endpoints = communications.MapGroup("/callback-endpoints");
        endpoints.MapGet("/", async (IDispatcher d, CancellationToken ct) => (await d.Query(new ListCommunicationCallbackEndpoints(), ct)).ToHttp(Results.Ok))
            .Produces<IReadOnlyList<CommunicationCallbackEndpointView>>(StatusCodes.Status200OK)
            .WithName("CommunicationsListCallbackEndpoints")
            .WithSummary("The tenant's delivery callback endpoints, without their keys or secrets (ADR-023)")
            .RequirePermission(CommunicationPermissions.CallbackEndpointRead);

        endpoints.MapPost("/", async (CommunicationCallbackEndpointRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new IssueCommunicationCallbackEndpoint(r.ProviderCode, r.RequireClientToken), ct))
                .ToHttp(issued => Results.Created($"/api/v1/communications/callback-endpoints/{issued.Endpoint.Id}", issued)))
            .Produces<IssuedCommunicationCallbackEndpoint>(StatusCodes.Status201Created)
            .WithName("CommunicationsIssueCallbackEndpoint")
            .WithSummary("Issue a messaging provider's delivery callback endpoint; an active one is rotated (ADR-023)")
            .WithDescription("The key and signing secret are shown once, in this response. The rotated endpoint is still accepted until Communications:Callbacks:Overlap "
                + "(7 days) has passed. Audited.")
            .RequireIdempotencyKey()
            .OnboardingConfiguration()
            .RequirePermission(CommunicationPermissions.CallbackEndpointUpdate);

        endpoints.MapPost("/{id:guid}/revoke", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RevokeCommunicationCallbackEndpoint(id), ct)).ToHttp(Results.Ok))
            .Produces<CommunicationCallbackEndpointView>(StatusCodes.Status200OK)
            .WithName("CommunicationsRevokeCallbackEndpoint")
            .WithSummary("Revoke a delivery callback endpoint at once, for a leaked key or secret (ADR-023)")
            .OnboardingConfiguration()
            .RequirePermission(CommunicationPermissions.CallbackEndpointUpdate);

        communications.MapPost("/callbacks/{endpointKey}", async (string endpointKey, HttpContext http, ICommunicationCallbackResolver resolver, IServiceScopeFactory scopes,
                IAuthorizationService authorization, CancellationToken ct) =>
            {
                using var reader = new StreamReader(http.Request.Body);
                var body = await reader.ReadToEndAsync(ct);

                // One answer for an unknown key, a revoked endpoint, a bad signature or an old timestamp, so none can be told apart.
                var refused = Problems.For(BuildingBlocks.Domain.ErrorCodes.CallbackSignatureInvalid, "The callback is not authenticated.", StatusCodes.Status401Unauthorized);
                if (!CallbackKeys.Plausible(endpointKey) || await resolver.Resolve(CallbackKeys.Hash(endpointKey), ct) is not { } found)
                {
                    return refused;
                }

                var clientToken = http.User.Identity?.IsAuthenticated == true && http.User.FindFirst("tenant_id")?.Value == found.TenantId.ToString();

                // The tenant comes only from the key, and nothing is applied without the signature keyed to that tenant and provider.
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<ExecutionScope>().InitialiseForService(found.TenantId, "communication-callback");
                var result = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new ReceiveDeliveryCallback(found.EndpointId, body,
                    http.Request.Headers["X-Callback-Timestamp"], http.Request.Headers["X-Callback-Id"], http.Request.Headers["X-Signature"], clientToken), ct);
                if (result.IsFailure)
                {
                    return Problems.For(result.Error);
                }

                return result.Value switch
                {
                    DeliveryCallbackResult.Applied or DeliveryCallbackResult.Unknown => Results.NoContent(),
                    DeliveryCallbackResult.Replayed => Problems.For(BuildingBlocks.Domain.ErrorCodes.CallbackReplayed, "This callback ID was already received.",
                        StatusCodes.Status409Conflict),
                    DeliveryCallbackResult.Invalid => Problems.For(BuildingBlocks.Domain.ErrorCodes.ValidationFailed,
                        "The callback needs providerRef and status (DELIVERED, BOUNCED or FAILED).", StatusCodes.Status422UnprocessableEntity),
                    _ => refused,
                };
            })
            .Accepts<DeliveryCallbackRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("CommunicationsSignedDeliveryCallback")
            .WithSummary("A messaging provider reports a message's delivery at its callback address (ADR-023, §5.1)")
            .WithDescription("Headers X-Callback-Timestamp (Unix seconds), X-Callback-Id and X-Signature: HMAC-SHA256 of \"timestamp.callbackId.body\" under the "
                + "endpoint's signing secret, hex. 204 when applied, and also for an unknown provider reference, so the provider does not retry it forever.")
            .RequireSignedProviderCallback();
    }

    /// <summary>The member portal (§8): a member's own notifications and preferences, and their devices for push.</summary>
    private static void MapPortal(IEndpointRouteBuilder api)
    {
        const string member = ExternalSubject.Party;
        var portal = api.MapGroup("/portal/communications").WithTags("Communications");

        portal.MapGet("/notifications", async (string? cursor, int? limit, IDispatcher d, CancellationToken ct) =>
                await Page(cursor, limit, (after, size) => d.Query(new ListPortalNotifications(after, size), ct), n => n.Id))
            .Produces<CursorPage<PortalNotificationView>>(StatusCodes.Status200OK)
            .WithName("PortalCommunicationsListNotifications")
            .WithSummary("The member's own notifications of types the tenant shows in the portal, newest first (§8)")
            .RequirePermission(CommunicationPermissions.NotificationRead).ForPortal(member);

        portal.MapGet("/notifications/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetPortalNotification(id), ct)).ToHttp(Results.Ok))
            .Produces<PortalNotificationView>(StatusCodes.Status200OK)
            .WithName("PortalCommunicationsGetNotification")
            .WithSummary("One of the member's own notifications; another's is not found (§8)")
            .RequirePermission(CommunicationPermissions.NotificationRead).ForPortal(member);

        portal.MapGet("/preferences", async (IDispatcher d, CancellationToken ct) => (await d.Query(new GetMyPreferences(), ct)).ToHttp(Results.Ok))
            .Produces<IReadOnlyList<PreferenceView>>(StatusCodes.Status200OK)
            .WithName("PortalCommunicationsGetPreferences")
            .WithSummary("The member's own channel preferences (§4)")
            .RequirePermission(CommunicationPermissions.PreferenceRead).ForPortal(member);

        portal.MapPut("/preferences/{typeCode}", async (string typeCode, PreferenceRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SetMyPreference(typeCode, r.Channels ?? [], r.OptedOut), ct)).ToHttp(Results.Ok))
            .Produces<PreferenceView>(StatusCodes.Status200OK)
            .WithName("PortalCommunicationsSetPreference")
            .WithSummary("The member sets their channels for a type, or opts out of a marketing type (§4)")
            .WithEventualEffects("an opt-out reaches the member's consent ledger as a withdrawal.")
            .RequirePermission(CommunicationPermissions.PreferenceUpdate).ForPortal(member);

        portal.MapPost("/devices", async (DeviceRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RegisterDevice(r.Platform, r.Token), ct)).ToHttp(v => Results.Created($"/api/v1/portal/communications/devices/{v.Id}", v)))
            .Produces<DeviceView>(StatusCodes.Status201Created)
            .WithName("PortalCommunicationsRegisterDevice")
            .WithSummary("A portal app registers the member's device for push (§3); the token is kept encrypted")
            .RequireIdempotencyKey()
            .RequirePermission(CommunicationPermissions.DeviceUpdate).ForPortal(member);

        portal.MapDelete("/devices/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Send(new RevokeDevice(id), ct)).ToHttp(Results.Ok))
            .Produces<DeviceView>(StatusCodes.Status200OK)
            .WithName("PortalCommunicationsRevokeDevice")
            .WithSummary("The member removes one of their devices; push stops at once")
            .RequirePermission(CommunicationPermissions.DeviceUpdate).ForPortal(member);
    }

    /// <summary>A cursor page of a list query.</summary>
    private static async Task<IResult> Page<T>(string? cursor, int? limit, Func<Guid?, int, Task<BuildingBlocks.Domain.Result<IReadOnlyList<T>>>> query, Func<T, Guid> id)
    {
        if (!Cursor.TryDecode(cursor, out var after))
        {
            return Problems.For(BuildingBlocks.Domain.ErrorCodes.ValidationFailed, "The cursor is not valid.", StatusCodes.Status422UnprocessableEntity);
        }

        var size = Cursor.ClampLimit(limit);
        return (await query(after, size)).ToHttp(items => Results.Ok(new CursorPage<T>(items, items.Count == size ? Cursor.Encode(id(items[^1])) : null)));
    }
}
