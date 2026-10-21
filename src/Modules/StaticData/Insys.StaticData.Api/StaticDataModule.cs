using Insys.BuildingBlocks.Api;
using Insys.BuildingBlocks.Api.StaticData;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.StaticData.Api.StaticData;
using Insys.StaticData.Application.Changes;
using Insys.StaticData.Contracts;
using Insys.StaticData.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.StaticData.Api;

public sealed record Tier2MakerCheckerRequest(bool Tier2MakerChecker);

/// <summary>
/// The Static Data module: its own reference data (sdm, refdata and ops schemas) and the governance of every static
/// data change in the platform. Approve and reject are decided in Workflow (ADR-009), which maps them on the same paths
/// as each table's lifecycle actions.
/// </summary>
public sealed class StaticDataModule : IModule
{
    public string Name => "StaticData";

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddStaticDataInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        StaticDataStaticDataEndpoints.MapStaticData(api);
        const string tag = "StaticData governance";

        var governance = api.MapGroup("").WithMetadata(new StaticDataEndpointMetadata());

        var changes = governance.MapGroup("/static-data/change-requests").WithTags(tag);
        changes.MapGet("/", async (string? status, string? entityCode, string? cursor, int? limit, IDispatcher dispatcher, CancellationToken ct) =>
            {
                if (!Cursor.TryDecode(cursor, out var afterId))
                {
                    return Problems.For(BuildingBlocks.Domain.ErrorCodes.ValidationFailed, "The cursor is not valid.", StatusCodes.Status422UnprocessableEntity);
                }

                var pageSize = Cursor.ClampLimit(limit);
                return (await dispatcher.Query(new ListChangeRequests(status, entityCode, afterId, pageSize), ct))
                    .ToHttp(items => Results.Ok(new CursorPage<ChangeRequestView>(items, items.Count == pageSize ? Cursor.Encode(items[^1].Id) : null)));
            })
            .WithName("ListChangeRequests")
            .WithSummary("List static data change requests")
            .RequirePermission(StaticDataGovernancePermissions.ChangeRequestRead);

        changes.MapGet("/{id:guid}", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Query(new GetChangeRequest(id), ct)).ToHttp(Results.Ok))
            .WithName("GetChangeRequest")
            .WithSummary("Get a static data change request")
            .RequirePermission(StaticDataGovernancePermissions.ChangeRequestRead);

        var settings = governance.MapGroup("/static-data/settings").WithTags(tag);
        settings.MapGet("/", async (IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                (await dispatcher.Query(new GetGovernanceSettings(), ct)).ToHttp(view =>
                {
                    Concurrency.SetETag(http, view.RowVersion);
                    return Results.Ok(view);
                }))
            .WithName("GetGovernanceSettings")
            .WithSummary("Get the tenant's static data governance settings")
            .WithDescription("Tier 1 maker-checker is always on; Tier 2 is on unless the tenant switches it off (SDM-003).")
            .RequirePermission(StaticDataGovernancePermissions.SettingRead);

        settings.MapPut("/tier2-maker-checker", async (Tier2MakerCheckerRequest request, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
            {
                var expected = Concurrency.ExpectedVersion(http.Request);
                if (expected.IsFailure)
                {
                    return Problems.For(expected.Error);
                }

                return (await dispatcher.Send(new ChangeTier2MakerChecker(request.Tier2MakerChecker, expected.Value), ct)).ToHttp(version =>
                {
                    Concurrency.SetETag(http, version);
                    return Results.NoContent();
                });
            })
            .WithName("ChangeTier2MakerChecker")
            .WithSummary("Switch Tier 2 maker-checker on or off for the tenant")
            .WithDescription("Audited. Use the ETag from GET; it is \"0\" until the setting is first changed.")
            .RequirePermission(StaticDataGovernancePermissions.SettingUpdate);

        governance.MapGroup("/static-data/imports").WithTags(tag).MapGet("/", async (string? cursor, int? limit, IDispatcher dispatcher, CancellationToken ct) =>
            {
                if (!Cursor.TryDecode(cursor, out var afterId))
                {
                    return Problems.For(BuildingBlocks.Domain.ErrorCodes.ValidationFailed, "The cursor is not valid.", StatusCodes.Status422UnprocessableEntity);
                }

                var pageSize = Cursor.ClampLimit(limit);
                return (await dispatcher.Query(new ListImportBatches(afterId, pageSize), ct))
                    .ToHttp(items => Results.Ok(new CursorPage<ImportBatchView>(items, items.Count == pageSize ? Cursor.Encode(items[^1].Id) : null)));
            })
            .WithName("ListImportBatches")
            .WithSummary("List static data import batches and their rejected lines (SDM-012)")
            .RequirePermission(StaticDataGovernancePermissions.ChangeRequestRead);
    }
}
