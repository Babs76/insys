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

public sealed class IntegrationModule : IModule
{
    public string Name => "Integration";

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
            .WithDescription("Returns the dead letter with its original event envelope. The ETag is required to replay or discard it.")
            .Produces<DeadLetterDetail>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(IntegrationPermissions.DeadLetterRead);

        MapResolution(deadLetters, "replay", "ReplayMessage", "Replay a dead-lettered message",
            "Delivers the original event to its consumers again. Consumers skip events they already processed (INT-005, INT-006).",
            (id, version) => new ReplayMessage(id, version));

        MapResolution(deadLetters, "discard", "DiscardMessage", "Discard a dead-lettered message",
            "Closes the dead letter without delivering it (INT-006).",
            (id, version) => new DiscardMessage(id, version));

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
