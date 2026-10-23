using Insys.Analytics.Api.StaticData;
using Insys.Analytics.Application;
using Insys.Analytics.Contracts;
using Insys.Analytics.Infrastructure;
using Insys.BuildingBlocks.Api;
using Insys.BuildingBlocks.Application.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Analytics.Api;

/// <summary>
/// The Analytics module (§22): read-only projections for portfolio and claims analysis, built from the events it
/// consumes and rebuilt from its own log of them.
/// </summary>
/// <summary>How many logged events a rebuild replayed.</summary>
public sealed record ProjectionsRebuilt(int Replayed);

public sealed class AnalyticsModule : IModule
{
    public string Name => "Analytics";

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddAnalyticsInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        AnalyticsStaticDataEndpoints.MapStaticData(api);
        var analytics = api.MapGroup("/analytics").WithTags(Name);

        analytics.MapGet("/written-premium", async (DateOnly from, DateOnly to, IDispatcher d, CancellationToken ct) =>
                (await d.Query(new WrittenPremiumQuery(from, to), ct)).ToHttp(Results.Ok))
            .Produces<IReadOnlyList<WrittenPremiumRow>>(StatusCodes.Status200OK)
            .WithName("AnalyticsWrittenPremium")
            .WithSummary("Written premium and policy count by day, product, channel and branch")
            .RequirePermission(AnalyticsPermissions.ProjectionRead);

        analytics.MapGet("/claims-incurred", async (string fromMonth, string toMonth, IDispatcher d, CancellationToken ct) =>
                (await d.Query(new ClaimsIncurredQuery(fromMonth, toMonth), ct)).ToHttp(Results.Ok))
            .Produces<IReadOnlyList<ClaimsIncurredRow>>(StatusCodes.Status200OK)
            .WithName("AnalyticsClaimsIncurred")
            .WithSummary("Claims incurred and claim count by month (yyyy-MM), product and cause")
            .RequirePermission(AnalyticsPermissions.ProjectionRead);

        analytics.MapPost("/projections/rebuild", async (IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RebuildProjections(), ct)).ToHttp(replayed => Results.Ok(new ProjectionsRebuilt(replayed))))
            .Produces<ProjectionsRebuilt>(StatusCodes.Status200OK)
            .WithName("AnalyticsRebuildProjections")
            .WithSummary("Clear the tenant's projections and rebuild them from the events Analytics consumed")
            .RequirePermission(AnalyticsPermissions.ProjectionUpdate);
    }
}
