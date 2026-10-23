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

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddComplianceInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        ComplianceStaticDataEndpoints.MapStaticData(api);
        var compliance = api.MapGroup("/compliance").WithTags(Name);

        compliance.MapPost("/returns", async (PrepareReturn r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(r, ct)).ToHttp(view => Results.Created($"/api/v1/compliance/returns/{view.Id}", view)))
            .Produces<ReturnView>(StatusCodes.Status201Created)
            .WithName("CompliancePrepareReturn")
            .WithSummary("Prepare a regulatory return for the period it ends with, from its definition's field mapping; one per code and period")
            .RequirePermission(CompliancePermissions.ReturnCreate);

        compliance.MapGet("/returns", async (string? periodCode, ReturnStatus? status, IDispatcher d, CancellationToken ct) =>
                (await d.Query(new ListReturns(periodCode, status), ct)).ToHttp(Results.Ok))
            .Produces<IReadOnlyList<ReturnView>>(StatusCodes.Status200OK)
            .WithName("ComplianceListReturns")
            .WithSummary("Regulatory returns, by period or status")
            .RequirePermission(CompliancePermissions.ReturnRead);

        compliance.MapGet("/returns/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetReturn(id), ct)).ToHttp(Results.Ok))
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
            .WithName("ComplianceRunReport")
            .WithSummary("Run a report in the catalogue, if your roles allow it; export only where the catalogue allows")
            .RequirePermission(CompliancePermissions.ReportCreate);

        compliance.MapGet("/report-runs/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetReportRun(id), ct)).ToHttp(Results.Ok))
            .Produces<ReportRunView>(StatusCodes.Status200OK)
            .WithName("ComplianceGetReportRun")
            .WithSummary("A report run and its result")
            .RequirePermission(CompliancePermissions.ReportRead);
    }

    private static void Action(RouteGroupBuilder group, string route, ReturnAction action, string summary, string permission) =>
        group.MapPost($"/returns/{{id:guid}}/{route}", async (Guid id, ReturnReasonRequest? r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ChangeReturn(id, action, r?.Reason), ct)).ToHttp(Results.Ok))
            .Produces<ReturnView>(StatusCodes.Status200OK)
            .WithName($"Compliance{action}Return")
            .WithSummary(summary)
            .RequirePermission(permission);
}
