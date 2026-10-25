/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Integration
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
using Insys.BuildingBlocks.Application.Messaging;
using Insys.Integration.Api.StaticData;
using Insys.Integration.Application.DeadLetters;
using Insys.Integration.Application.Parking;
using Insys.Integration.Application.Webhooks;
using Insys.Integration.Domain.Webhooks;
using Insys.Integration.Contracts;
using Insys.Integration.Domain.DeadLetters;
using Insys.Integration.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Integration.Api;

public sealed record ParkedRetryRequest(string Code);

public sealed record ParkedRetryAccepted(int Count);

public sealed record ParkedDiscardRequest(string Reason);

public sealed record ParkingSettingRequest(string ConfigurationOwnerRole, string? CalendarCode, string MoneyFirst, string MoneySecond, string OtherFirst, string OtherSecond);

public sealed class IntegrationModule : IModule
{
    public string Name => "Integration";

    public IReadOnlyList<BuildingBlocks.Api.Functions.FunctionDeclaration> Functions => IntegrationFunctions.All;

    /// <summary>A platform module: open to the operator tenant (ADR-024).</summary>
    public bool HoldsBusinessData => false;

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddIntegrationInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        IntegrationStaticDataEndpoints.MapStaticData(api);

        var deadLetters = api.MapGroup("/integration/dead-letters").WithTags(Name);

        deadLetters.MapGet("/", async (string? cursor, int? limit, DeadLetterStatus? status, string? source, IDispatcher dispatcher, CancellationToken ct) =>
            {
                if (!Cursor.TryDecode(cursor, out var afterId))
                {
                    return Problems.For(BuildingBlocks.Domain.ErrorCodes.ValidationFailed, "The cursor is not valid.", StatusCodes.Status422UnprocessableEntity);
                }

                var pageSize = Cursor.ClampLimit(limit);
                var result = await dispatcher.Query(new ListDeadLetters(status, source, afterId, pageSize), ct);
                return result.ToHttp(items => Results.Ok(new CursorPage<DeadLetterSummary>(items, items.Count == pageSize ? Cursor.Encode(items[^1].Id) : null)));
            })
            .Produces<CursorPage<DeadLetterSummary>>(StatusCodes.Status200OK)
            .WithName("ListDeadLetters")
            .WithSummary("List dead-lettered messages")
            .WithDescription("Messages whose delivery failed after all retries (INT-006), oldest first, with cursor pagination.")
            .Produces<CursorPage<DeadLetterSummary>>()
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .RequirePermission(IntegrationPermissions.DeadLetterRead);

        deadLetters.MapGet("/{id:guid}", async (Guid id, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
            {
                var result = await dispatcher.Query(new GetDeadLetter(id), ct);
                return result.ToHttp(detail =>
                {
                    Concurrency.SetETag(http, detail.RowVersion);
                    return Results.Ok(detail);
                });
            })
            .Produces<DeadLetterDetail>(StatusCodes.Status200OK)
            .WithName("GetDeadLetter")
            .WithSummary("Get a dead-lettered message")
            .WithDescription("Returns the dead letter with its original event envelope. The ETag is required to replay or discard it. error never holds "
                + "the message body or personal data: it is the failing exception's type, \"HTTP <status>\" from a webhook endpoint, or a fixed sentence "
                + "(BR-017). The payload is the event as published: it carries identifiers only (INT-007), but the console may still hide it.")
            .Produces<DeadLetterDetail>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(IntegrationPermissions.DeadLetterRead);

        MapResolution(deadLetters, "replay", "ReplayMessage", "Replay a dead-lettered message",
            "Delivers the original event to its consumers again. Consumers skip events they already processed (INT-005, INT-006).",
            (id, version) => new ReplayMessage(id, version));

        MapResolution(deadLetters, "discard", "DiscardMessage", "Discard a dead-lettered message",
            "Closes the dead letter without delivering it (INT-006).",
            (id, version) => new DiscardMessage(id, version));

        api.MapGroup("/integration/deliveries").WithTags(Name).MapGet("/summary", async (IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Query(new Application.Parking.DeliveriesSummaryQuery(), ct)).ToHttp(Results.Ok))
            .Produces<BuildingBlocks.Application.DashboardSummary>(StatusCodes.Status200OK)
            .WithName("IntegrationDeliveriesSummary")
            .WithSummary("Dashboard figures (GAP-046): parked deliveries by prerequisite, failing deliveries, open dead letters, failing webhooks, oldest parked")
            .WithDescription("Figures: DELIVERIES_PARKED (by prerequisite code), DELIVERIES_FAILING, DEAD_LETTERS_OPEN, WEBHOOK_DELIVERIES_FAILING, OLDEST_PARKED_AT, "
                + "and JOB_FUNCTIONS_OFF (count, by the JOB function codes the tenant switched off, whose work waits: OQ-098). Definitions: "
                + "docs/specs/addenda/dashboard-figures.md.")
            .RequirePermission(IntegrationPermissions.DeadLetterRead);

        MapParkedDeliveries(api.MapGroup("/integration/parked-deliveries").WithTags(Name));
        MapParkingSetting(api.MapGroup("/integration/parking-setting").WithTags(Name));

        var webhooks = api.MapGroup("/integration/webhook-deliveries").WithTags(Name);

        webhooks.MapGet("/", async (WebhookDeliveryStatus? status, string? subscriptionId, Guid? after, int? limit, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Query(new ListDeliveries(status, subscriptionId, after, limit ?? 50), ct)).ToHttp(Results.Ok))
            .Produces<IReadOnlyList<WebhookDeliveryView>>(StatusCodes.Status200OK)
            .WithName("ListWebhookDeliveries")
            .WithSummary("Partner webhook deliveries, by status or subscription, with their latest attempt (INT-006)")
            .RequirePermission(IntegrationPermissions.WebhookRead);

        webhooks.MapPost("/{id:guid}/retry", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new RetryDelivery(id), ct)).ToHttp(Results.Ok))
            .Produces<WebhookDeliveryView>(StatusCodes.Status200OK)
            .WithName("RetryWebhookDelivery")
            .WithSummary("Attempt a pending delivery at the next run of the webhook job instead of at its back-off time")
            .RequirePermission(IntegrationPermissions.WebhookUpdate);
    }

    /// <summary>ADR-022: deliveries parked for missing tenant configuration, waiting behind them, or failing.</summary>
    private static void MapParkedDeliveries(RouteGroupBuilder parked)
    {
        AuditTargetTypes.Register("ParkedDelivery");

        parked.MapGet("/", async (LedgerStatus? status, string? code, string? consumer, string? aggregateType, Guid? aggregateId, string? cursor, int? limit,
                IDispatcher dispatcher, CancellationToken ct) =>
            {
                if (!Cursor.TryDecode(cursor, out var afterId))
                {
                    return Problems.For(BuildingBlocks.Domain.ErrorCodes.ValidationFailed, "The cursor is not valid.", StatusCodes.Status422UnprocessableEntity);
                }

                var pageSize = Cursor.ClampLimit(limit);
                var result = await dispatcher.Query(new ListParkedDeliveries(new ParkedDeliveryFilter(status, code, consumer, aggregateType, aggregateId), afterId, pageSize), ct);
                return result.ToHttp(items => Results.Ok(new CursorPage<ParkedDeliveryView>(items, items.Count == pageSize ? Cursor.Encode(items[^1].Id) : null)));
            })
            .WithName("ListParkedDeliveries")
            .WithSummary("List parked, waiting and failing deliveries in the tenant")
            .WithDescription("ADR-022: each delivery's consumer, record, event type, missing configuration (code and item), parked since, attempts and escalation, "
                + "filtered by status, code, consumer and record, with cursor pagination.")
            .Produces<CursorPage<ParkedDeliveryView>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .RequirePermission(IntegrationPermissions.DeadLetterRead);

        parked.MapGet("/by-tenant", async (IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Query(new ListParkedDeliveriesByTenant(), ct)).ToHttp(Results.Ok))
            .WithName("ListParkedDeliveriesByTenant")
            .WithSummary("Count parked deliveries per tenant and kind (platform operator)")
            .WithDescription("ADR-022 decision 5: for each active tenant, its parked deliveries by consumer, code and item, with the highest escalation level and the oldest.")
            .Produces<IReadOnlyList<ParkedDeliveryCount>>(StatusCodes.Status200OK)
            .RequirePlatformPermission(IntegrationPermissions.DeadLetterRead);

        parked.MapGet("/{id:guid}", async (Guid id, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                (await dispatcher.Query(new GetParkedDelivery(id), ct)).ToHttp(view =>
                {
                    Concurrency.SetETag(http, view.RowVersion);
                    return Results.Ok(view);
                }))
            .WithName("GetParkedDelivery")
            .WithSummary("Get a parked delivery")
            .WithDescription("The ETag is required to discard it.")
            .Produces<ParkedDeliveryView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(IntegrationPermissions.DeadLetterRead);

        parked.MapPost("/{id:guid}/retry", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new RetryParkedDeliveries(id, null), ct)).ToHttp(count => Results.Accepted(value: new ParkedRetryAccepted(count))))
            .WithName("RetryParkedDelivery")
            .WithSummary("Retry a parked delivery at the parking job's next pass")
            .WithDescription("ADR-022 decision 4, trigger 3. The consumer's inbox skips the event if it was already applied, so a retry never applies it twice.")
            .Produces<ParkedRetryAccepted>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(IntegrationPermissions.DeadLetterUpdate);

        parked.MapPost("/retry", async (ParkedRetryRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new RetryParkedDeliveries(null, request.Code), ct)).ToHttp(count => Results.Accepted(value: new ParkedRetryAccepted(count))))
            .WithName("RetryParkedDeliveriesForCode")
            .WithSummary("Retry every parked delivery in the tenant for one code")
            .Produces<ParkedRetryAccepted>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .RequirePermission(IntegrationPermissions.DeadLetterUpdate);

        parked.MapPost("/{id:guid}/discard", async (Guid id, ParkedDiscardRequest request, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
            {
                var expected = Concurrency.ExpectedVersion(http.Request);
                if (expected.IsFailure)
                {
                    return Problems.For(expected.Error);
                }

                return (await dispatcher.Send(new DiscardParkedDelivery(id, expected.Value, request.Reason), ct)).ToHttp(version =>
                {
                    Concurrency.SetETag(http, version);
                    return Results.NoContent();
                });
            })
            .RequireIfMatch()
            .WithName("DiscardParkedDelivery")
            .WithSummary("Discard a delivery without delivering it, with a reason")
            .WithDescription("ADR-022 decision 6: never automatic; the reason is required and the discard is audited. Deliveries waiting behind it are then released.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .RequirePermission(IntegrationPermissions.DeadLetterUpdate);
    }

    /// <summary>ADR-022 decision 6: the tenant's escalation of parked deliveries.</summary>
    private static void MapParkingSetting(RouteGroupBuilder setting)
    {
        AuditTargetTypes.Register("ParkingSetting");

        setting.MapGet("/", async (IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                (await dispatcher.Query(new GetParkingSetting(), ct)).ToHttp(view =>
                {
                    Concurrency.SetETag(http, view.RowVersion);
                    return Results.Ok(view);
                }))
            .WithName("GetParkingSetting")
            .WithSummary("Get the tenant's escalation of parked deliveries")
            .WithDescription("The defaults (isDefault true) until the tenant sets its own: the configuration owner role, the business calendar, and ISO 8601 business-time "
                + "durations for money consumers (PT4H, then P1D) and all others (P1D, then P3D).")
            .Produces<ParkingSettingView>(StatusCodes.Status200OK)
            .RequirePermission(IntegrationPermissions.DeadLetterRead);

        setting.MapPut("/", async (ParkingSettingRequest r, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
            {
                long? expected = null;
                if (http.Request.Headers.IfMatch.Count > 0)
                {
                    var version = Concurrency.ExpectedVersion(http.Request);
                    if (version.IsFailure)
                    {
                        return Problems.For(version.Error);
                    }

                    expected = version.Value;
                }

                return (await dispatcher.Send(new ChangeParkingSetting(r.ConfigurationOwnerRole, r.CalendarCode, r.MoneyFirst, r.MoneySecond, r.OtherFirst,
                    r.OtherSecond, expected), ct)).ToHttp(version =>
                {
                    Concurrency.SetETag(http, version);
                    return Results.NoContent();
                });
            })
            .WithName("ChangeParkingSetting")
            .WithSummary("Set the tenant's escalation of parked deliveries")
            .WithDescription("Audited. If-Match is required once the tenant has its own setting.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .RequirePermission(IntegrationPermissions.ParkingSettingUpdate);
    }

    private static void MapResolution(RouteGroupBuilder group, string action, string name, string summary, string description,
        Func<Guid, long, ICommand<long>> command) =>
        group.MapPost($"/{{id:guid}}/{action}", async (Guid id, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
            {
                var expected = Concurrency.ExpectedVersion(http.Request);
                if (expected.IsFailure)
                {
                    return Problems.For(expected.Error);
                }

                var result = await dispatcher.Send(command(id, expected.Value), ct);
                return result.ToHttp(version =>
                {
                    Concurrency.SetETag(http, version);
                    return Results.NoContent();
                });
            })
            .RequireIfMatch()
            .WithName(name)
            .WithSummary(summary)
            .WithDescription(description)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequirePermission(IntegrationPermissions.DeadLetterUpdate);
}
