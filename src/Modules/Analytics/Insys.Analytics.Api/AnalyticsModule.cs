/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Analytics
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
            .WithSummary("Written premium and policy count by day, product, channel and branch, within your scope (F103)")
            .RequirePermission(AnalyticsPermissions.ProjectionRead);

        analytics.MapGet("/claims-incurred", async (string fromMonth, string toMonth, IDispatcher d, CancellationToken ct) =>
                (await d.Query(new ClaimsIncurredQuery(fromMonth, toMonth), ct)).ToHttp(Results.Ok))
            .Produces<IReadOnlyList<ClaimsIncurredRow>>(StatusCodes.Status200OK)
            .WithName("AnalyticsClaimsIncurred")
            .WithSummary("Claims incurred and claim count by month (yyyy-MM), product, cause and branch, within your scope (F103)")
            .RequirePermission(AnalyticsPermissions.ProjectionRead);

        analytics.MapGet("/summary", async (DateOnly from, DateOnly to, IDispatcher d, CancellationToken ct) =>
                (await d.Query(new AnalyticsSummaryQuery(from, to), ct)).ToHttp(Results.Ok))
            .Produces<BuildingBlocks.Application.DashboardSummary>(StatusCodes.Status200OK)
            .WithName("AnalyticsSummary")
            .WithSummary("Dashboard figures (GAP-046): written premium, policies written, claims incurred and claims reported, within your scope")
            .WithDescription("WRITTEN_PREMIUM (per currency) and POLICIES_WRITTEN over the days from and to, both included; CLAIMS_INCURRED (per currency, the net "
                + "change in claim reserves) and CLAIMS_REPORTED over the months those days fall in. General and Life only: notIncluded lists HEALTH. "
                + "Definitions: docs/specs/addenda/dashboard-figures.md.")
            .RequirePermission(AnalyticsPermissions.ProjectionRead);

        analytics.MapPost("/projections/rebuild", async (IDispatcher d, CancellationToken ct) =>
                (await d.Send(new RebuildProjections(), ct)).ToHttp(replayed => Results.Ok(new ProjectionsRebuilt(replayed))))
            .Produces<ProjectionsRebuilt>(StatusCodes.Status200OK)
            .WithName("AnalyticsRebuildProjections")
            .WithSummary("Clear the tenant's projections and rebuild them from the events Analytics consumed")
            .RequirePermission(AnalyticsPermissions.ProjectionUpdate);
    }
}
