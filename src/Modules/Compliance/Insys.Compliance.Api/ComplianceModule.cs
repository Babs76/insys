/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Compliance
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
using Insys.Compliance.Api.StaticData;
using Insys.Compliance.Application;
using Insys.Compliance.Contracts;
using Insys.Compliance.Domain;
using Insys.Compliance.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Compliance.Api;

public sealed record ReturnReasonRequest(string? Reason);

/// <summary>
/// The Compliance and Reporting module (§24): regulatory returns prepared from the ledger and the projections, reviewed,
/// approved by a compliance officer and submitted through the regulator adapter; and operational reports under the
/// catalogue's access rules.
/// </summary>
public sealed class ComplianceModule : IModule
{
    public string Name => "Compliance";

    public IReadOnlyList<BuildingBlocks.Api.Functions.FunctionDeclaration> Functions => ComplianceFunctions.All;

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddComplianceInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        ComplianceStaticDataEndpoints.MapStaticData(api);
        var compliance = api.MapGroup("/compliance").WithTags(Name);

        compliance.MapPost("/returns", async (PrepareReturn r, IDispatcher d, HttpContext http, CancellationToken ct) =>
                (await d.Send(r, ct)).ToHttp(view => Tagged(http, view, Results.Created($"/api/v1/compliance/returns/{view.Id}", view))))
            .Produces<ReturnView>(StatusCodes.Status201Created)
            .RequireIdempotencyKey()
            .WithName("CompliancePrepareReturn")
            .WithSummary("Prepare a regulatory return for the period it ends with, from its definition's field mapping; one per code and period")
            .RequirePermission(CompliancePermissions.ReturnCreate);

        compliance.MapGet("/returns", async (string? periodCode, ReturnStatus? status, IDispatcher d, CancellationToken ct) =>
                (await d.Query(new ListReturns(periodCode, status), ct)).ToHttp(Results.Ok))
            .Produces<IReadOnlyList<ReturnView>>(StatusCodes.Status200OK)
            .WithName("ComplianceListReturns")
            .WithSummary("Regulatory returns, by period or status")
            .RequirePermission(CompliancePermissions.ReturnRead);

        compliance.MapGet("/returns/summary", async (string fromPeriod, string toPeriod, IDispatcher d, CancellationToken ct) =>
                (await d.Query(new ReturnsSummaryQuery(fromPeriod, toPeriod), ct)).ToHttp(Results.Ok))
            .Produces<BuildingBlocks.Application.DashboardSummary>(StatusCodes.Status200OK)
            .WithName("ComplianceReturnsSummary")
            .WithSummary("Dashboard figures (GAP-046): returns by status, awaiting action, and overdue, for the periods fromPeriod to toPeriod (yyyy-MM)")
            .WithDescription("RETURNS_OVERDUE counts returns not yet submitted past their definition's submissionDays after the period end. A definition without "
                + "submissionDays has no deadline: its returns are never overdue, and its code is listed in by as NO_DEADLINE:<code> (OQ-083).")
            .RequirePermission(CompliancePermissions.ReturnRead);

        compliance.MapGet("/returns/{id:guid}", async (Guid id, IDispatcher d, HttpContext http, CancellationToken ct) =>
                (await d.Query(new GetReturn(id), ct)).ToHttp(view => Tagged(http, view, Results.Ok(view))))
            .Produces<ReturnView>(StatusCodes.Status200OK)
            .WithName("ComplianceGetReturn")
            .WithSummary("A regulatory return and its values")
            .RequirePermission(CompliancePermissions.ReturnRead);

        Action(compliance, "review", ReturnAction.Review, "Review a prepared return; the reviewer is not its preparer", CompliancePermissions.ReturnUpdate);
        Action(compliance, "approve", ReturnAction.Approve, "Approve a reviewed return, as a compliance officer other than its preparer and reviewer",
            CompliancePermissions.ReturnApprove);
        Action(compliance, "submit", ReturnAction.Submit, "Submit an approved return to its regulator through the adapter", CompliancePermissions.ReturnUpdate);
        Action(compliance, "accept", ReturnAction.Accept, "Record that the regulator accepted a submitted return", CompliancePermissions.ReturnUpdate);
        Action(compliance, "reject", ReturnAction.Reject, "Record that the regulator rejected a submitted return, with its reason", CompliancePermissions.ReturnUpdate);
        Action(compliance, "correct", ReturnAction.Correct, "Correct a rejected return: its values are calculated again and it returns to Prepared",
            CompliancePermissions.ReturnUpdate);

        compliance.MapPost("/report-runs", async (RunReport r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(r, ct)).ToHttp(view => Results.Created($"/api/v1/compliance/report-runs/{view.Id}", view)))
            .Produces<ReportRunView>(StatusCodes.Status201Created)
            .RequireIdempotencyKey()
            .WithName("ComplianceRunReport")
            .WithSummary("Run a report in the catalogue, if your roles allow it; export only where the catalogue allows")
            .RequirePermission(CompliancePermissions.ReportCreate);

        compliance.MapGet("/report-runs", async (string? reportCode, DateTimeOffset? from, DateTimeOffset? to, int? limit, IDispatcher d, CancellationToken ct) =>
                (await d.Query(new ListReportRuns(reportCode, from, to, limit ?? 50), ct)).ToHttp(Results.Ok))
            .Produces<IReadOnlyList<ReportRunSummary>>(StatusCodes.Status200OK)
            .WithName("ComplianceListReportRuns")
            .WithSummary("Report runs, newest first: by report and by when they ran (from inclusive, to exclusive); limit 1 to 200, default 50")
            .RequirePermission(CompliancePermissions.ReportRead);

        compliance.MapGet("/report-runs/{id:guid}/export", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new DownloadReportExport(id), ct)).ToHttp(file => Results.File(file.Content, file.ContentType, file.FileName)))
            .Produces<string>(StatusCodes.Status200OK, "text/csv")
            .WithName("ComplianceDownloadReportExport")
            .WithSummary("The CSV file of a run made with export (one line per metric), while the catalogue lets your roles export the report")
            .WithDescription("REPORT_NOT_EXPORTED (409) for a run made without export; REPORT_NOT_ALLOWED (403) when the catalogue no longer allows you. Each download is audited.")
            .RequirePermission(CompliancePermissions.ReportRead);

        compliance.MapGet("/report-runs/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetReportRun(id), ct)).ToHttp(Results.Ok))
            .Produces<ReportRunView>(StatusCodes.Status200OK)
            .WithName("ComplianceGetReportRun")
            .WithSummary("A report run and its result")
            .RequirePermission(CompliancePermissions.ReportRead);
    }

    /// <summary>A return action with the ETag the caller read (If-Match, DBA-011); the response carries the new one (GAP-048).</summary>
    private static void Action(RouteGroupBuilder group, string route, ReturnAction action, string summary, string permission) =>
        group.MapPost($"/returns/{{id:guid}}/{route}", async (Guid id, ReturnReasonRequest? r, IDispatcher d, HttpContext http, CancellationToken ct) =>
            {
                var expected = Concurrency.ExpectedVersion(http.Request);
                if (expected.IsFailure)
                {
                    return Problems.For(expected.Error);
                }

                return (await d.Send(new ChangeReturn(id, action, r?.Reason, expected.Value), ct)).ToHttp(view => Tagged(http, view, Results.Ok(view)));
            })
            .Produces<ReturnView>(StatusCodes.Status200OK)
            .RequireIfMatch()
            .WithName($"Compliance{action}Return")
            .WithSummary(summary)
            .RequirePermission(permission);

    private static IResult Tagged(HttpContext http, ReturnView view, IResult result)
    {
        Concurrency.SetETag(http, view.RowVersion);
        return result;
    }
}
