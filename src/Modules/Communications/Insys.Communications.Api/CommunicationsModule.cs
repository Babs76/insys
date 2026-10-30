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

    public IReadOnlyList<BuildingBlocks.Api.Functions.FunctionDeclaration> Functions => CommunicationsFunctions.All;

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddCommunicationsInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        CommunicationsStaticDataEndpoints.MapStaticData(api);
        AuditTargetTypes.Register(nameof(BuildingBlocks.Domain.ProviderCallbacks.CallbackEndpoint));
        var communications = api.MapGroup("/communications").WithTags(Name);

        communications.MapGet("/notifications", async (Guid? partyId, string? typeCode, NotificationStatus? status, string? sourceType, Guid? sourceId,
                DateTimeOffset? from, DateTimeOffset? to, string? cursor, int? limit, IDispatcher d, CancellationToken ct) =>
                await Page(cursor, limit, (after, size) => d.Query(new ListNotifications(partyId, typeCode, status, sourceType, sourceId, after, size, from, to), ct),
                    n => n.Id))
            .Produces<CursorPage<NotificationSummary>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("CommunicationsListNotifications")
            .WithSummary("Notifications, by recipient party, type, status or source record, oldest first (§5)")
            .WithDescription("Within the caller's scope (F103): a notification is in scope when the product and organisation node of the record it is about are. "
                + "from/to filter on requestedAt. sourceType is GENERAL_CLAIM, LIFE_CLAIM, HEALTH_CLAIM, HEALTH_PREAUTH, GENERAL_POLICY or BILLING_ACCOUNT; "
                + "sourceReference is that record's own number. sourceId needs sourceType (BR-090): sourceId alone is refused with 422 "
                + "VALIDATION_FAILED, since record IDs are unique only within a source type.")
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .RequirePermission(CommunicationPermissions.NotificationRead);

        communications.MapGet("/notifications/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetNotification(id), ct)).ToHttp(Results.Ok))
            .Produces<NotificationView>(StatusCodes.Status200OK)
            .WithName("CommunicationsGetNotification")
            .WithSummary("A notification: the text as sent, and every delivery attempt with the address masked (§5)")
            .AlsoChecks(Health.Contracts.HealthPermissions.ClaimRead,
                "a Health notice (sourceType HEALTH_CLAIM or HEALTH_PREAUTH): without it, subject and body are null and bodyRedacted is true (HLT-008)")
            .RequirePermission(CommunicationPermissions.NotificationRead);

        communications.MapGet("/parties/{partyId:guid}/preferences", async (Guid partyId, IDispatcher d, CancellationToken ct) =>
                (await d.Query(new GetPreferences(partyId), ct)).ToHttp(Results.Ok))
            .Produces<IReadOnlyList<PreferenceView>>(StatusCodes.Status200OK)
            .WithName("CommunicationsGetPreferences")
            .WithSummary("Every notification type the party can receive, with their channels, whether they can opt out, and their consent for marketing (§4)")
            .WithDescription("Each preference is its own record: its rowVersion, quoted (\"2\"), is the If-Match for setting that type's preference, and the set "
                + "returns the new one in its ETag. rowVersion is null for a type the party has no preference for yet, which is set without If-Match (BR-089).")
            .RequirePermission(CommunicationPermissions.PreferenceRead);

        communications.MapPut("/parties/{partyId:guid}/preferences/{typeCode}", async (Guid partyId, string typeCode, PreferenceRequest r, IDispatcher d, HttpContext http,
                CancellationToken ct) =>
                Concurrency.OptionalExpectedVersion(http.Request) is { IsSuccess: true } expected
                    ? (await d.Send(new SetPreference(partyId, typeCode, r.Channels ?? [], r.OptedOut, expected.Value), ct)).ToHttp(view =>
                    {
                        if (view.RowVersion is { } version)
                        {
                            Concurrency.SetETag(http, version);
                        }

                        return Results.Ok(view);
                    })
                    : Problems.For(BuildingBlocks.Domain.ErrorCodes.PreconditionFailed, "If-Match is not a version ETag.", StatusCodes.Status412PreconditionFailed))
            .Produces<PreferenceView>(StatusCodes.Status200OK)
            .ReturnsETag()
            .AllowIfMatch()
            .WithName("CommunicationsSetPreference")
            .WithSummary("Set a party's channels for a type, in order, or opt out of a marketing type (§4)")
            .WithDescription("A whole replacement: channels in order (null or empty only when opting out). A service notice keeps at least one channel "
                + "(PREFERENCE_NOT_ALLOWED to opt out). Opting out of marketing also records the withdrawal of its consent purpose in Party. optedOut false never "
                + "grants consent: only Party's consents/grant does, and that also clears an opt-out; a withdrawal in Party sets optedOut. Audited.")
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

        endpoints.MapPost("/", async (CommunicationCallbackEndpointRequest r, IDispatcher d, HttpContext http, CancellationToken ct) =>
                (await d.Send(new IssueCommunicationCallbackEndpoint(r.ProviderCode, r.RequireClientToken), ct)).ToHttp(issued =>
                {
                    http.Response.Headers.CacheControl = "no-store";
                    return Results.Created($"/api/v1/communications/callback-endpoints/{issued.Endpoint.Id}", issued);
                }))
            .Produces<IssuedCommunicationCallbackEndpoint>(StatusCodes.Status201Created)
            .WithName("CommunicationsIssueCallbackEndpoint")
            .WithSummary("Issue a messaging provider's delivery callback endpoint; an active one is rotated (ADR-023)")
            .WithDescription("The key and signing secret are shown once, in this response (Cache-Control: no-store). The key is part of the callback URL, so keep it "
                + "out of logs. The rotated endpoint is still accepted until Communications:Callbacks:Overlap (7 days) has passed. Audited. The response is not "
                + "kept: a repeat with the same Idempotency-Key is refused with 409 IDEMPOTENCY_RESPONSE_NOT_KEPT.")
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequireIdempotencyKey()
            .ShowsSecretOnce()
            .OnboardingConfiguration()
            .RequirePermission(CommunicationPermissions.CallbackEndpointUpdate);

        endpoints.MapPost("/{id:guid}/revoke", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RevokeCommunicationCallbackEndpoint(id), ct)).ToHttp(Results.Ok))
            .Produces<CommunicationCallbackEndpointView>(StatusCodes.Status200OK)
            .WithName("CommunicationsRevokeCallbackEndpoint")
            .WithSummary("Revoke a delivery callback endpoint at once, for a leaked key or secret (ADR-023)")
            .WithDescription("Revoking an endpoint already revoked is refused with 409 INVALID_STATE_TRANSITION.")
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

                // Optional extra (ADR-023 decision 4): a client-credentials token for this tenant holding communication_callback.create.
                var clientToken = http.User.Identity?.IsAuthenticated == true
                                  && http.User.FindFirst("tenant_id")?.Value == found.TenantId.ToString()
                                  && (await authorization.AuthorizeAsync(http.User, PermissionPolicyProvider.Prefix + CommunicationPermissions.CallbackCreate)).Succeeded;

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
            .WithDescription("No bearer token: the tenant and provider come only from endpointKey, and providerRef is matched only among that tenant's attempts "
                + "with that provider. An unknown, retired or revoked key, a bad signature or a timestamp outside the tolerance (5 minutes) is 401 "
                + "CALLBACK_SIGNATURE_INVALID, the same answer for all. A callback ID already received at the endpoint (kept for twice the tolerance) is 409 "
                + "CALLBACK_REPLAYED. 204 when applied, and also for an unknown provider reference, so the provider does not retry it forever. A delivered "
                + "message stays delivered: a later bounce or failure for it changes nothing.")
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
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("PortalCommunicationsListNotifications")
            .WithSummary("The member's own notifications that were sent, of types the tenant shows in the portal, newest first (§8)")
            .WithDescription("For member (party) links. Brokers and providers are refused (EXTERNAL_USER_NOT_ALLOWED): no Wave 1 notice is addressed to them. "
                + "A notice about a dependant under 18 goes to the principal and says only its status.")
            .RequirePermission(CommunicationPermissions.NotificationRead).ForPortal(member);

        portal.MapGet("/notifications/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetPortalNotification(id), ct)).ToHttp(Results.Ok))
            .Produces<PortalNotificationView>(StatusCodes.Status200OK)
            .WithName("PortalCommunicationsGetNotification")
            .WithSummary("One of the member's own notifications; another's is not found (§8)")
            .RequirePermission(CommunicationPermissions.NotificationRead).ForPortal(member);

        portal.MapGet("/preferences", async (IDispatcher d, CancellationToken ct) => (await d.Query(new GetMyPreferences(), ct)).ToHttp(Results.Ok))
            .Produces<IReadOnlyList<PreferenceView>>(StatusCodes.Status200OK)
            .WithName("PortalCommunicationsGetPreferences")
            .WithSummary("Every notification type the member can receive, their channels, whether they can opt out, and their consent for marketing (§4)")
            .RequirePermission(CommunicationPermissions.PreferenceRead).ForPortal(member);

        portal.MapPut("/preferences/{typeCode}", async (string typeCode, PreferenceRequest r, IDispatcher d, HttpContext http, CancellationToken ct) =>
                Concurrency.OptionalExpectedVersion(http.Request) is { IsSuccess: true } expected
                    ? (await d.Send(new SetMyPreference(typeCode, r.Channels ?? [], r.OptedOut, expected.Value), ct)).ToHttp(Results.Ok)
                    : Problems.For(BuildingBlocks.Domain.ErrorCodes.PreconditionFailed, "If-Match is not a version ETag.", StatusCodes.Status412PreconditionFailed))
            .Produces<PreferenceView>(StatusCodes.Status200OK)
            .AllowIfMatch()
            .WithName("PortalCommunicationsSetPreference")
            .WithSummary("The member sets their channels for a type, or opts out of a marketing type (§4)")
            .WithEventualEffects("an opt-out reaches the member's consent ledger as a withdrawal.")
            .RequirePermission(CommunicationPermissions.PreferenceUpdate).ForPortal(member);

        portal.MapGet("/devices", async (IDispatcher d, CancellationToken ct) => (await d.Query(new ListMyDevices(), ct)).ToHttp(Results.Ok))
            .Produces<IReadOnlyList<DeviceView>>(StatusCodes.Status200OK)
            .WithName("PortalCommunicationsListDevices")
            .WithSummary("The member's devices registered for push, revoked ones included; never the token")
            .RequirePermission(CommunicationPermissions.DeviceUpdate).ForPortal(member);

        portal.MapPost("/devices", async (DeviceRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RegisterDevice(r.Platform, r.Token), ct)).ToHttp(v => Results.Created($"/api/v1/portal/communications/devices/{v.Id}", v)))
            .Produces<DeviceView>(StatusCodes.Status201Created)
            .WithName("PortalCommunicationsRegisterDevice")
            .WithSummary("A portal app registers the member's device for push (§3); the token is kept encrypted")
            .WithDescription("The same token registered again by the same member returns the device already registered.")
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
