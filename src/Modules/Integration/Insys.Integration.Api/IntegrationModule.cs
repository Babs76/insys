using Insys.BuildingBlocks.Api;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.Integration.Api.StaticData;
using Insys.Integration.Application.DeadLetters;
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
            .WithName(name)
            .WithSummary(summary)
            .WithDescription(description)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .RequirePermission(IntegrationPermissions.DeadLetterUpdate);
}
