/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Reinsurance
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
using Insys.Reinsurance.Api.StaticData;
using Insys.Reinsurance.Application;
using Insys.Reinsurance.Contracts;
using Insys.Reinsurance.Domain;
using Insys.Reinsurance.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Reinsurance.Api;

public sealed record SettleRecoveryRequest(decimal Amount, DateOnly SettledOn);

/// <summary>
/// The Reinsurance module (§17): cessions under treaties and facultative arrangements, reproducible from the versions
/// they name (REI-002) and linked to their policies (REI-001); recoveries on settled claims, billed and settled
/// traceably (REI-003); and bordereaux per treaty and period, sent through the reinsurer adapter.
/// </summary>
public sealed class ReinsuranceModule : IModule
{
    public string Name => "Reinsurance";

    /// <summary>Business commands wait for the tenant to be activated (onboarding readiness).</summary>
    public bool TransactsBusiness => true;

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddReinsuranceInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        ReinsuranceStaticDataEndpoints.MapStaticData(api);
        var reinsurance = api.MapGroup("/reinsurance").WithTags(Name);

        reinsurance.MapGet("/cessions", async (string sourceDomain, Guid policyRef, IDispatcher d, CancellationToken ct) =>
                (await d.Query(new ListCessions(policyRef, sourceDomain), ct)).ToHttp(Results.Ok))
            .Produces<IReadOnlyList<CessionView>>(StatusCodes.Status200OK)
            .WithName("ReinsuranceListCessions")
            .WithSummary("A policy's cessions, superseded ones included, with the treaty, rule and gross amounts they were calculated from (REI-001, REI-002)")
            .RequirePermission(ReinsurancePermissions.CessionRead);

        reinsurance.MapGet("/recoveries", async (string? claimNumber, RecoveryStatus? status, Guid? after, int? limit, IDispatcher d, CancellationToken ct) =>
                (await d.Query(new ListRecoveries(claimNumber, status, after, limit ?? 50), ct)).ToHttp(Results.Ok))
            .Produces<IReadOnlyList<RecoveryView>>(StatusCodes.Status200OK)
            .WithName("ReinsuranceListRecoveries")
            .WithSummary("Recoveries, by claim or status, with cursor pagination")
            .RequirePermission(ReinsurancePermissions.RecoveryRead);

        reinsurance.MapGet("/recoveries/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetRecovery(id), ct)).ToHttp(Results.Ok))
            .Produces<RecoveryView>(StatusCodes.Status200OK)
            .WithName("ReinsuranceGetRecovery")
            .WithSummary("A recovery, with what was recoverable, billed and settled (REI-003)")
            .RequirePermission(ReinsurancePermissions.RecoveryRead);

        reinsurance.MapPost("/recoveries/{id:guid}/bill", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new BillRecovery(id), ct)).ToHttp(Results.Ok))
            .Produces<RecoveryView>(StatusCodes.Status200OK)
            .WithName("ReinsuranceBillRecovery")
            .WithSummary("Bill an identified recovery to the reinsurer through the adapter")
            .RequirePermission(ReinsurancePermissions.RecoveryUpdate);

        reinsurance.MapPost("/recoveries/{id:guid}/settle", async (Guid id, SettleRecoveryRequest r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SettleRecovery(id, r.Amount, r.SettledOn), ct)).ToHttp(Results.Ok))
            .Produces<RecoveryView>(StatusCodes.Status200OK)
            .WithName("ReinsuranceSettleRecovery")
            .WithSummary("Record what the reinsurer paid on a billed recovery; a difference stays visible for reconciliation (REI-003)")
            .RequirePermission(ReinsurancePermissions.RecoveryUpdate);

        reinsurance.MapPost("/bordereaux", async (PrepareBordereau r, IDispatcher d, CancellationToken ct) =>
                (await d.Send(r, ct)).ToHttp(view => Results.Created($"/api/v1/reinsurance/bordereaux/{view.Id}", view)))
            .Produces<BordereauView>(StatusCodes.Status201Created)
            .WithName("ReinsurancePrepareBordereau")
            .WithSummary("Prepare a treaty's bordereau for a period from its layout; one per treaty and period")
            .RequirePermission(ReinsurancePermissions.BordereauCreate);

        reinsurance.MapGet("/bordereaux/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) => (await d.Query(new GetBordereau(id), ct)).ToHttp(Results.Ok))
            .Produces<BordereauView>(StatusCodes.Status200OK)
            .WithName("ReinsuranceGetBordereau")
            .WithSummary("A bordereau and its rows")
            .RequirePermission(ReinsurancePermissions.BordereauRead);

        reinsurance.MapPost("/bordereaux/{id:guid}/send", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new SendBordereau(id), ct)).ToHttp(Results.Ok))
            .Produces<BordereauView>(StatusCodes.Status200OK)
            .WithName("ReinsuranceSendBordereau")
            .WithSummary("Send a prepared bordereau to the reinsurer through the adapter")
            .RequirePermission(ReinsurancePermissions.BordereauUpdate);

        reinsurance.MapPost("/bordereaux/{id:guid}/agree", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new AgreeBordereau(id), ct)).ToHttp(Results.Ok))
            .Produces<BordereauView>(StatusCodes.Status200OK)
            .WithName("ReinsuranceAgreeBordereau")
            .WithSummary("Record that the reinsurer agreed a sent bordereau")
            .RequirePermission(ReinsurancePermissions.BordereauUpdate);
    }
}
