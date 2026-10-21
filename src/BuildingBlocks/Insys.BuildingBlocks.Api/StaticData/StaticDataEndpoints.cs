using System.Text;
using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Insys.BuildingBlocks.Api.StaticData;

/// <summary>Permission codes for static data, in the iam.ref_permission_catalogue format (resource.operation).</summary>
public static class StaticDataPermissions
{
    public const string Read = "static_data.read";
    public const string Create = "static_data.create";
    public const string Update = "static_data.update";
    public const string Approve = "static_data.approve";
}

/// <summary>A bulk load of drafts (SDM-012).</summary>
public sealed record StaticDataImport<TInput>(string FileRef, IReadOnlyList<TInput> Lines);

public sealed record StaticDataImportResult(int Imported);

/// <summary>
/// The generated endpoints for one static data table, under /api/v1/{module}/static/{entity}. The version segment is
/// named versionNumber because {version} is the API version in /api/v{version}:
/// tenant rows on the module's connection, and global rows under /global for platform administrators only (scopes G
/// and GT). Approve and reject are mapped once, by the Static Data module.
/// </summary>
public static class StaticDataEndpoints
{
    private const string Actions = "regex(^(submit|activate|deprecate|retire)$)";

    public static void MapStaticEntity<TDbContext, TEntity, TInput>(this IEndpointRouteBuilder api, StaticDataDescriptor descriptor, string tag)
        where TDbContext : ModuleDbContext
        where TEntity : StaticRecord<TInput>, new()
        where TInput : StaticRecordInput
    {
        var group = api.MapGroup($"/{descriptor.ModuleRoute}/static/{descriptor.EntityRoute}").WithTags(tag)
            .WithMetadata(new StaticDataEndpointMetadata());
        var name = descriptor.EntityName;

        group.MapGet("/", async (DateOnly? asAt, string? cursor, int? limit, StaticDataService<TDbContext> service, CancellationToken ct) =>
            {
                var pageSize = Cursor.ClampLimit(limit);
                if (!TryDecodeCode(cursor, out var afterCode))
                {
                    return Problems.For(ErrorCodes.ValidationFailed, "The cursor is not valid.", StatusCodes.Status422UnprocessableEntity);
                }

                var items = await service.ListInForce<TEntity>(descriptor, asAt ?? DateOnly.FromDateTime(DateTime.UtcNow), afterCode, pageSize, ct);
                return Results.Ok(new CursorPage<TEntity>(items, items.Count == pageSize ? EncodeCode(items[^1].Code) : null));
            })
            .WithName($"List{Pascal(descriptor)}")
            .WithSummary($"List {name} in force")
            .WithDescription($"{name} records in force on asAt (default today), tenant rows over global baselines (SDM-008, SDM-015).")
            .RequirePermission(StaticDataPermissions.Read);

        group.MapGet("/{code}", async (string code, DateOnly? asAt, StaticDataService<TDbContext> service, HttpContext http, CancellationToken ct) =>
            {
                var entity = await service.GetInForce<TEntity>(descriptor, code, asAt ?? DateOnly.FromDateTime(DateTime.UtcNow), ct);
                if (entity is null)
                {
                    return Problems.For(ErrorCodes.NotFound, $"No {name} with this code is in force on that date.", StatusCodes.Status404NotFound);
                }

                Concurrency.SetETag(http, entity.RowVersion);
                return Results.Ok(entity);
            })
            .WithName($"Get{Pascal(descriptor)}")
            .WithSummary($"Get the {name} version in force")
            .RequirePermission(StaticDataPermissions.Read);

        MapScope<TDbContext, TEntity, TInput>(group, descriptor, global: false, name);
        if (descriptor.AllowsGlobalRows)
        {
            MapScope<TDbContext, TEntity, TInput>(group.MapGroup("/global"), descriptor, global: true, name);
        }
    }

    private static void MapScope<TDbContext, TEntity, TInput>(RouteGroupBuilder scope, StaticDataDescriptor d, bool global, string entityName)
        where TDbContext : ModuleDbContext
        where TEntity : StaticRecord<TInput>, new()
        where TInput : StaticRecordInput
    {
        {
            var suffix = global ? "Global" : "";
            var owner = global ? "global (platform administrators)" : "tenant";

            scope.MapGet("/{code}/versions", async (string code, StaticDataService<TDbContext> service, CancellationToken ct) =>
                    Results.Ok(await service.Versions<TEntity>(code, global, ct)))
                .WithName($"List{Pascal(d)}Versions{suffix}")
                .WithSummary($"List every {owner} version of a {entityName} code")
                .Secure(global, StaticDataPermissions.Read);

            if (global ? !d.AllowsGlobalRows : !d.AllowsTenantRows)
            {
                return;
            }

            scope.MapPost("/", async (TInput input, StaticDataService<TDbContext> service, HttpContext http, CancellationToken ct) =>
                    (await service.CreateDraft<TEntity, TInput>(d, input, global, ct)).ToHttp(entity =>
                    {
                        Concurrency.SetETag(http, entity.RowVersion);
                        return Results.Created($"{http.Request.Path.Value!.TrimEnd('/')}/{entity.Code}/versions/{entity.Version}", entity);
                    }))
                .WithName($"Create{Pascal(d)}Draft{suffix}")
                .WithSummary($"Create a {owner} {entityName} draft")
                .WithDescription("Creates version 1 of a new code, or the next version of an existing one. The caller is the maker.")
                .Secure(global, StaticDataPermissions.Create)
                .RequireIdempotencyKey();

            scope.MapPut("/{code}/versions/{versionNumber:int}", async (string code, int versionNumber, TInput input, StaticDataService<TDbContext> service, HttpContext http,
                    CancellationToken ct) =>
                {
                    var expected = Concurrency.ExpectedVersion(http.Request);
                    return expected.IsFailure
                        ? Problems.For(expected.Error)
                        : (await service.EditDraft<TEntity, TInput>(d, code, versionNumber, expected.Value, input, global, ct)).ToHttp(entity => Ok(http, entity));
                })
                .WithName($"Edit{Pascal(d)}Draft{suffix}")
                .WithSummary($"Edit a {owner} {entityName} draft")
                .WithDescription("Only the maker edits a draft; approved versions are immutable (SDM-002).")
                .Secure(global, StaticDataPermissions.Update);

            scope.MapPost($"/{{code}}/versions/{{versionNumber:int}}/{{action:{Actions}}}", async (string code, int versionNumber, string action,
                    StaticDataService<TDbContext> service, HttpContext http, CancellationToken ct) =>
                {
                    var expected = Concurrency.ExpectedVersion(http.Request);
                    if (expected.IsFailure)
                    {
                        return Problems.For(expected.Error);
                    }

                    var result = action switch
                    {
                        "submit" => await service.Submit<TEntity>(d, code, versionNumber, expected.Value, global, ct),
                        "activate" => await service.Activate<TEntity>(d, code, versionNumber, expected.Value, global, ct),
                        "deprecate" => await service.Deprecate<TEntity>(d, code, versionNumber, expected.Value, global, ct),
                        _ => await service.Retire<TEntity>(d, code, versionNumber, expected.Value, global, ct),
                    };
                    return result.ToHttp(entity => Ok(http, entity));
                })
                .WithName($"Change{Pascal(d)}Lifecycle{suffix}")
                .WithSummary($"Submit, activate, deprecate or retire a {owner} {entityName} version")
                .WithDescription("submit sends the draft for approval (approve and reject are on the same path, handled by Static Data); "
                    + "activate puts an approved version in force; deprecate stops new use; retire hides it (SDM-007).")
                .Secure(global, StaticDataPermissions.Update);

            scope.MapPost("/imports", async (StaticDataImport<TInput> import, StaticDataService<TDbContext> service, CancellationToken ct) =>
                    (await service.Import<TEntity, TInput>(d, import.Lines, import.FileRef, global, ct)).ToHttp(count => Results.Ok(new StaticDataImportResult(count))))
                .WithName($"Import{Pascal(d)}{suffix}")
                .WithSummary($"Import {owner} {entityName} drafts, all or nothing")
                .WithDescription("Every line is validated; if any line fails, nothing is saved and each rejected line is reported (SDM-012).")
                .Secure(global, StaticDataPermissions.Create)
                .RequireIdempotencyKey();
        }
    }

    private static RouteHandlerBuilder Secure(this RouteHandlerBuilder builder, bool global, string permission) =>
        global ? builder.RequirePlatformPermission(permission) : builder.RequirePermission(permission);

    private static IResult Ok(HttpContext http, StaticRecord entity)
    {
        Concurrency.SetETag(http, entity.RowVersion);
        return Results.Ok(entity);
    }

    /// <summary>Endpoint names are global, and table names repeat across modules (for example AllocationRules), so the module is part of the name.</summary>
    private static string Pascal(StaticDataDescriptor descriptor) => descriptor.EntityType.Namespace!.Split('.')[1] + descriptor.EntityType.Name;

    private static string EncodeCode(string code) => System.Buffers.Text.Base64Url.EncodeToString(Encoding.UTF8.GetBytes(code));

    private static bool TryDecodeCode(string? cursor, out string? code)
    {
        code = null;
        if (string.IsNullOrEmpty(cursor))
        {
            return true;
        }

        try
        {
            code = Encoding.UTF8.GetString(System.Buffers.Text.Base64Url.DecodeFromChars(cursor));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
