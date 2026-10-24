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

public sealed record OnboardingStepRequest(string Step);

public sealed record IsolationProfileRequest(IsolationProfile IsolationProfile, string ApprovedMigrationRef);

/// <summary>
/// The Tenancy module. Tenant administration is for the platform operator tenant only (TEN-007, TEN-008); tenants
/// themselves never see these endpoints.
/// </summary>
public sealed class TenancyModule : IModule
{
    public string Name => "Tenancy";

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
                Versioned(http, version => dispatcher.Send(new CompleteOnboardingStep(id, request.Step, version), ct)))
            .Produces(StatusCodes.Status204NoContent)
            .RequireIfMatch()
            .WithName("CompleteOnboardingStep")
            .WithSummary("Complete the current onboarding step")
            .WithDescription("Steps follow the tenant load sequence in order; activation needs them all (TEN-008).")
            .RequirePlatformPermission(TenancyPermissions.Update);

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
