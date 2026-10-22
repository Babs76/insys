using Insys.BuildingBlocks.Api;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.Distribution.Api.StaticData;
using Insys.Distribution.Application;
using Insys.Distribution.Contracts;
using Insys.Distribution.Domain.Commission;
using Insys.Distribution.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Distribution.Api;

public sealed record ClawBackRequest(string Reason);

/// <summary>
/// The Distribution module (§16): intermediaries, their product permissions (DST-001) and lifecycle as static data;
/// commission accrued at the plan version in force on the invoice (DST-002, DST-003), made payable when the invoice is
/// paid, clawed back on cancellation or lapse, and paid through statements approved by a second person (DST-004).
/// </summary>
public sealed class DistributionModule : IModule
{
    public string Name => "Distribution";

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddDistributionInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        DistributionStaticDataEndpoints.MapStaticData(api);
        var distribution = api.MapGroup("/distribution").WithTags(Name);

        distribution.MapGet("/intermediaries/{code}/permissions/{productCode}", async (string code, string productCode, IDispatcher d, CancellationToken ct) =>
                (await d.Query(new CheckIntermediary(code, productCode), ct)).ToHttp(Results.Ok))
            .WithName("DistributionCheckIntermediary")
            .WithSummary("Whether the intermediary may sell the product today: in the register, not suspended, with a permission in force (DST-001)")
            .RequirePermission(DistributionPermissions.IntermediaryRead);

        distribution.MapGet("/entitlements", async (string? intermediaryCode, Guid? policyRef, EntitlementStatus? status, Guid? after, int? limit, IDispatcher d,
                    CancellationToken ct) =>
                (await d.Query(new ListEntitlements(intermediaryCode, policyRef, status, after, limit ?? 50), ct)).ToHttp(Results.Ok))
            .WithName("DistributionListEntitlements")
            .WithSummary("Commission entitlements, by intermediary, policy or status, with cursor pagination")
            .RequirePermission(DistributionPermissions.CommissionRead);

        distribution.MapGet("/entitlements/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Query(new GetEntitlement(id), ct)).ToHttp(Results.Ok))
            .WithName("DistributionGetEntitlement")
            .WithSummary("A commission entitlement, with its plan version and source transaction (DST-003) and any clawback")
            .RequirePermission(DistributionPermissions.CommissionRead);

        distribution.MapPost("/entitlements/{id:guid}/claw-back", async (Guid id, ClawBackRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ClawBackCommission(id, r.Reason), ct)).ToHttp(Results.Ok))
            .WithName("DistributionClawBack")
            .WithSummary("Claw back an accrued or payable entitlement in full; one already on a statement is recovered on the next")
            .RequirePermission(DistributionPermissions.CommissionUpdate);

        distribution.MapPost("/statements", async (PrepareStatement r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(r, ct)).ToHttp(view => Results.Created($"/api/v1/distribution/statements/{view.Id}", view)))
            .WithName("DistributionPrepareStatement")
            .WithSummary("Prepare an intermediary's statement for a month, or prepare a draft again; a second person approves it in Workflow (DST-004)")
            .RequirePermission(DistributionPermissions.StatementCreate);

        distribution.MapGet("/statements/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Query(new GetStatement(id), ct)).ToHttp(Results.Ok))
            .WithName("DistributionGetStatement")
            .WithSummary("A commission statement")
            .RequirePermission(DistributionPermissions.StatementRead);
    }
}
