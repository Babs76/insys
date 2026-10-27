/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Distribution
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

    public IReadOnlyList<BuildingBlocks.Api.Functions.FunctionDeclaration> Functions => DistributionFunctions.All;

    /// <summary>Business commands wait for the tenant to be activated (onboarding readiness).</summary>
    public bool TransactsBusiness => true;

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddDistributionInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        DistributionStaticDataEndpoints.MapStaticData(api);
        var distribution = api.MapGroup("/distribution").WithTags(Name).ETagsFromRowVersion();

        distribution.MapGet("/intermediaries/{code}/permissions/{productCode}", async (string code, string productCode, IDispatcher d, CancellationToken ct) =>
                (await d.Query(new CheckIntermediary(code, productCode), ct)).ToHttp(Results.Ok))
            .Produces<IntermediaryCheckView>(StatusCodes.Status200OK)
            .WithName("DistributionCheckIntermediary")
            .WithSummary("Whether the intermediary may sell the product today: in the register, not suspended, with a permission in force (DST-001)")
            .RequirePermission(DistributionPermissions.IntermediaryRead);

        distribution.MapGet("/entitlements", async (string? intermediaryCode, Guid? policyRef, EntitlementStatus? status, Guid? after, int? limit, IDispatcher d,
                    CancellationToken ct) =>
                (await d.Query(new ListEntitlements(intermediaryCode, policyRef, status, after, limit ?? 50), ct)).ToHttp(Results.Ok))
            .Produces<IReadOnlyList<EntitlementView>>(StatusCodes.Status200OK)
            .WithName("DistributionListEntitlements")
            .WithSummary("Commission entitlements, by intermediary, policy or status, with cursor pagination")
            .RequirePermission(DistributionPermissions.CommissionRead);

        distribution.MapGet("/entitlements/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Query(new GetEntitlement(id), ct)).ToHttp(Results.Ok))
            .Produces<EntitlementView>(StatusCodes.Status200OK)
            .WithName("DistributionGetEntitlement")
            .WithSummary("A commission entitlement, with its plan version and source transaction (DST-003) and any clawback")
            .RequirePermission(DistributionPermissions.CommissionRead);

        distribution.MapPost("/entitlements/{id:guid}/claw-back", async (Guid id, ClawBackRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new ClawBackCommission(id, r.Reason), ct)).ToHttp(Results.Ok))
            .Produces<EntitlementView>(StatusCodes.Status200OK)
            .TakesIfMatch()
            .WithName("DistributionClawBack")
            .WithSummary("Claw back an accrued or payable entitlement in full; one already on a statement is recovered on the next")
            .RequirePermission(DistributionPermissions.CommissionUpdate);

        distribution.MapPost("/statements", async (PrepareStatement r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(r, ct)).ToHttp(view => Results.Created($"/api/v1/distribution/statements/{view.Id}", view)))
            .Produces<StatementView>(StatusCodes.Status201Created)
            .RequireIdempotencyKey()
            .WithName("DistributionPrepareStatement")
            .WithSummary("Prepare an intermediary's statement for a month, or prepare a draft again; a second person approves it in Workflow (DST-004)")
            .RequirePermission(DistributionPermissions.StatementCreate);

        distribution.MapGet("/statements/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Query(new GetStatement(id), ct)).ToHttp(Results.Ok))
            .Produces<StatementView>(StatusCodes.Status200OK)
            .WithName("DistributionGetStatement")
            .WithSummary("A commission statement")
            .RequirePermission(DistributionPermissions.StatementRead);

        // The broker portal (external-user access §2): the broker's approved statements, and its sub-brokers'.
        const string broker = BuildingBlocks.Application.Context.ExternalSubject.Intermediary;
        var portal = api.MapGroup("/portal/distribution").WithTags(Name);
        portal.MapGet("/statements", async (Guid? afterId, int? limit, IDispatcher d, CancellationToken ct) =>
                (await d.Query(new ListPortalStatements(afterId, Math.Clamp(limit ?? 50, 1, 200)), ct)).ToHttp(Results.Ok))
            .Produces<IReadOnlyList<PortalStatementView>>(StatusCodes.Status200OK)
            .WithName("PortalDistributionListStatements")
            .WithSummary("The broker's approved and paid commission statements, and its sub-brokers', by ID after afterId")
            .RequirePermission(DistributionPermissions.StatementRead).ForPortal(broker);
        portal.MapGet("/statements/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Query(new GetPortalStatement(id), ct)).ToHttp(Results.Ok))
            .Produces<PortalStatementView>(StatusCodes.Status200OK)
            .WithName("PortalDistributionGetStatement")
            .WithSummary("An approved or paid commission statement of the broker or a sub-broker, without its approval or bank account")
            .RequirePermission(DistributionPermissions.StatementRead).ForPortal(broker);
    }
}
