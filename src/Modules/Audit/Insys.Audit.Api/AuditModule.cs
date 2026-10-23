using Insys.BuildingBlocks.Api;
using Insys.Audit.Api.StaticData;
using Insys.Audit.Infrastructure;
using Insys.Audit.Application;
using Insys.Audit.Contracts;
using Insys.BuildingBlocks.Application.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Audit.Api;

/// <summary>The Audit module. So far it serves its static data only; its aggregates are built in a later phase.</summary>
public sealed class AuditModule : IModule
{
    public string Name => "Audit";

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddAuditInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        AuditStaticDataEndpoints.MapStaticData(api);
        api.MapGroup("/audit/events").WithTags(Name).MapGet("/", async (string? targetType, string? targetId, string? actorId, string? cursor, int? limit,
                IDispatcher dispatcher, CancellationToken ct) =>
            {
                if (!Cursor.TryDecode(cursor, out var afterId))
                {
                    return Problems.For(BuildingBlocks.Domain.ErrorCodes.ValidationFailed, "The cursor is not valid.", StatusCodes.Status422UnprocessableEntity);
                }

                var pageSize = Cursor.ClampLimit(limit);
                var result = await dispatcher.Query(new ListAuditEvents(targetType, targetId, actorId, afterId, pageSize), ct);
                return result.ToHttp(items => Results.Ok(new CursorPage<AuditEventView>(items, items.Count == pageSize ? Cursor.Encode(items[^1].Id) : null)));
            })
            .Produces<CursorPage<AuditEventView>>(StatusCodes.Status200OK)
            .WithName("ListAuditEvents")
            .WithSummary("List audit events")
            .WithDescription("The tenant's audit trail of sensitive operations and denied requests, newest first (IAM-006, TEN-005).")
            .Produces<CursorPage<AuditEventView>>()
            .RequirePermission(AuditPermissions.Read);
    }
}
