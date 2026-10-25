/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Tenancy
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
using Insys.Tenancy.Api.StaticData;
using Insys.Tenancy.Application.Tenants;
using Insys.Tenancy.Contracts;
using Insys.Tenancy.Domain.Tenants;
using Insys.Tenancy.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Tenancy.Api;

public sealed record RegisterTenantRequest(string TenantCode, string LegalName, IsolationProfile IsolationProfile, string TimeZone);

public sealed record TenantStatusRequest(string Reason);

/// <param name="Attestations">Waivers for this step's checks that allow one (TREATIES, WEBHOOKS_OR_NONE), with the reason.</param>
/// <param name="Outcome">PASSED or FAILED.</param>
public sealed record SmokeRunRequest(string RunReference, DateOnly RunDate, [property: BuildingBlocks.Domain.OneOf("PASSED", "FAILED")] string Outcome, string RanBy);

public sealed record OnboardingStepRequest(string Step, IReadOnlyList<OnboardingAttestationRequest>? Attestations = null);

public sealed record IsolationProfileRequest(IsolationProfile IsolationProfile, string ApprovedMigrationRef);

/// <summary>
/// The Tenancy module. Tenant administration is for the platform operator tenant only (TEN-007, TEN-008); tenants
/// themselves never see these endpoints.
/// </summary>
public sealed class TenancyModule : IModule
{
    public string Name => "Tenancy";

    public IReadOnlyList<BuildingBlocks.Api.Functions.FunctionDeclaration> Functions => TenancyFunctions.All;

    /// <summary>A platform module: open to the operator tenant (ADR-024).</summary>
    public bool HoldsBusinessData => false;

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddTenancyInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        TenancyStaticDataEndpoints.MapStaticData(api);
        var tenants = api.MapGroup("/tenancy/tenants").WithTags(Name);

        tenants.MapPost("/", async (RegisterTenantRequest request, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
            {
                var tenantId = Guid.CreateVersion7();
                var result = await dispatcher.Send(new RegisterTenant(tenantId, request.TenantCode, request.LegalName, request.IsolationProfile, request.TimeZone), ct);
                return result.ToHttp(id =>
                {
                    Concurrency.SetETag(http, 1);
                    return Results.Created($"/api/v1/tenancy/tenants/{id}", new { id });
                });
            })
            .Produces<CreatedId>(StatusCodes.Status201Created)
            .WithName("RegisterTenant")
            .WithSummary("Register a tenant")
            .WithDescription("Creates the tenant with a new immutable identifier (TEN-001) and starts its onboarding case (TEN-008).")
            .RequirePlatformPermission(TenancyPermissions.Create)
            .RequireIdempotencyKey();

        tenants.MapGet("/", async (string? status, string? cursor, int? limit, IDispatcher dispatcher, CancellationToken ct) =>
            {
                if (!Cursor.TryDecode(cursor, out var afterId))
                {
                    return Problems.For(BuildingBlocks.Domain.ErrorCodes.ValidationFailed, "The cursor is not valid.", StatusCodes.Status422UnprocessableEntity);
                }

                var pageSize = Cursor.ClampLimit(limit);
                var result = await dispatcher.Query(new ListTenants(status, afterId, pageSize), ct);
                return result.ToHttp(items => Results.Ok(new CursorPage<TenantSummary>(items, items.Count == pageSize ? Cursor.Encode(items[^1].Id) : null)));
            })
            .Produces<CursorPage<TenantSummary>>(StatusCodes.Status200OK)
            .WithName("ListTenants")
            .WithSummary("List tenants")
            .RequirePlatformPermission(TenancyPermissions.Read);

        tenants.MapGet("/{id:guid}", async (Guid id, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                (await dispatcher.Query(new GetTenant(id), ct)).ToHttp(tenant =>
                {
                    Concurrency.SetETag(http, tenant.RowVersion);
                    return Results.Ok(tenant);
                }))
            .Produces<TenantDetail>(StatusCodes.Status200OK)
            .WithName("GetTenant")
            .WithSummary("Get a tenant with its onboarding case and status history")
            .RequirePlatformPermission(TenancyPermissions.Read);

        tenants.MapPost("/{id:guid}/complete-onboarding-step", (Guid id, OnboardingStepRequest request, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                Versioned(http, version => dispatcher.Send(new CompleteOnboardingStep(id, request.Step, version, request.Attestations), ct)))
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .RequireIfMatch()
            .WithName("CompleteOnboardingStep")
            .WithSummary("Complete the current onboarding step, once its readiness checks pass")
            .WithDescription("Steps follow the tenant load sequence in order; activation needs them all (TEN-008). The step's checks run in the tenant on its "
                + "business date; if any is not ready the step is refused with ONBOARDING_STEP_NOT_READY, whose errors name each check and what is missing, "
                + "and nothing changes. attestations record a waiver for TREATIES or WEBHOOKS_OR_NONE, or the end-to-end run's reference for SMOKE_RUN.")
            .RequirePlatformPermission(TenancyPermissions.Update);

        tenants.MapPost("/{id:guid}/smoke-run", (Guid id, SmokeRunRequest request, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                Versioned(http, version => dispatcher.Send(new RecordSmokeRun(id, request.RunReference, request.RunDate, request.Outcome, request.RanBy, version), ct)))
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .RequireIfMatch()
            .WithName("RecordSmokeRun")
            .WithSummary("Record the end-to-end run for the END_TO_END_TESTS step: its reference, date, outcome and who ran it")
            .WithDescription("The run is made in a non-production environment with the same configuration (decided 24 October 2026, until configuration "
                + "promotion between environments exists). It stands in for SMOKE_RUN once a second platform operator approves it and its outcome is PASSED.")
            .RequirePlatformPermission(TenancyPermissions.Update);

        tenants.MapPost("/{id:guid}/smoke-run/approve", (Guid id, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                Versioned(http, version => dispatcher.Send(new ApproveSmokeRun(id, version), ct)))
            .Produces(StatusCodes.Status204NoContent)
            .RequireIfMatch()
            .WithName("ApproveSmokeRun")
            .WithSummary("Approve the recorded end-to-end run, as a second platform operator (SRS-002): the one who recorded it cannot")
            .RequirePlatformPermission(TenancyPermissions.Update);

        tenants.MapGet("/{id:guid}/readiness", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Query(new GetTenantReadiness(id), ct)).ToHttp(Results.Ok))
            .Produces<TenantReadinessView>(StatusCodes.Status200OK)
            .WithName("GetTenantReadiness")
            .WithSummary("Every onboarding step's readiness checks and their results, without completing anything (onboarding readiness)")
            .RequirePlatformPermission(TenancyPermissions.Read);

        MapStatus(tenants, "activate", TenantStatusAction.Activate, "Activate a tenant whose onboarding is complete");
        MapStatus(tenants, "suspend", TenantStatusAction.Suspend, "Suspend a tenant; its users can no longer act");
        MapStatus(tenants, "reactivate", TenantStatusAction.Reactivate, "Reactivate a suspended tenant");
        MapStatus(tenants, "terminate", TenantStatusAction.Terminate, "Terminate a tenant");

        tenants.MapPost("/{id:guid}/change-isolation-profile", (Guid id, IsolationProfileRequest request, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                Versioned(http, version => dispatcher.Send(new ChangeIsolationProfile(id, request.IsolationProfile, request.ApprovedMigrationRef, version), ct)))
            .Produces(StatusCodes.Status204NoContent)
            .RequireIfMatch()
            .WithName("ChangeIsolationProfile")
            .WithSummary("Change a tenant's isolation profile")
            .WithDescription("Requires the reference of an approved data migration.")
            .RequirePlatformPermission(TenancyPermissions.Update);
    }

    private static void MapStatus(RouteGroupBuilder tenants, string action, TenantStatusAction statusAction, string summary) =>
        tenants.MapPost($"/{{id:guid}}/{action}", (Guid id, TenantStatusRequest request, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                Versioned(http, version => dispatcher.Send(new ChangeTenantStatus(id, statusAction, request.Reason, version), ct)))
            .Produces(StatusCodes.Status204NoContent)
            .RequireIfMatch()
            .WithName($"{statusAction}Tenant")
            .WithSummary(summary)
            .WithDescription("A reason is required and the change is kept in the status history (TEN-008).")
            .RequirePlatformPermission(TenancyPermissions.Update);

    private static async Task<IResult> Versioned(HttpContext http, Func<long, Task<BuildingBlocks.Domain.Result<long>>> send)
    {
        var expected = Concurrency.ExpectedVersion(http.Request);
        if (expected.IsFailure)
        {
            return Problems.For(expected.Error);
        }

        return (await send(expected.Value)).ToHttp(version =>
        {
            Concurrency.SetETag(http, version);
            return Results.NoContent();
        });
    }
}
