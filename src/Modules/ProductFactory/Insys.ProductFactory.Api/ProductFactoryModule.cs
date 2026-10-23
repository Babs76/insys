/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : ProductFactory
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
using Insys.ProductFactory.Api.StaticData;
using Insys.ProductFactory.Application;
using Insys.ProductFactory.Contracts;
using Insys.ProductFactory.Domain.Products;
using Insys.ProductFactory.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.ProductFactory.Api;

public sealed record RegisterProductRequest(string ProductCode, ProductDomain Domain, string FamilyCode, string? ClassCode, string Name, string[] Coverages,
    DateOnly EffectiveFrom);

public sealed record CreateDraftVersionRequest(DateOnly EffectiveFrom);

public sealed record EditConfigurationRequest(string Name, string[] Coverages, DateOnly EffectiveFrom, CoverStart? CoverStartsOn, string? EligibilityRulesetCode = null,
    ReinstatementArrears? ReinstatementArrears = null);

public sealed record SubmitForApprovalRequest(string? Reason);

public sealed record WithdrawVersionRequest(DateOnly EffectiveTo);

/// <summary>
/// The Product Factory module: product definitions and their governed versions (§7.7): Draft → Validated → Submitted
/// → Approved → Published → Effective, with simulation before submission and maker-checker approval in Workflow.
/// </summary>
public sealed class ProductFactoryModule : IModule
{
    public string Name => "ProductFactory";

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddProductFactoryInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        ProductFactoryStaticDataEndpoints.MapStaticData(api);
        var products = api.MapGroup("/product-factory/products").WithTags(Name);

        products.MapPost("/", async (RegisterProductRequest r, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                (await dispatcher.Send(new RegisterProduct(r.ProductCode, r.Domain, r.FamilyCode, r.ClassCode, r.Name, r.Coverages, r.EffectiveFrom), ct)).ToHttp(view =>
                {
                    Concurrency.SetETag(http, 1);
                    return Results.Created($"/api/v1/product-factory/products/{view.ProductCode}/versions/1", view with { RowVersion = 1 });
                }))
            .Produces<ProductVersionView>(StatusCodes.Status201Created)
            .WithName("ProductRegisterProduct")
            .WithSummary("Register a product: version 1, in Draft")
            .WithDescription("The code is unique in the tenant and never reused; the family must be in the product taxonomy for the domain.")
            .RequirePermission(ProductPermissions.ProductCreate)
            .RequireIdempotencyKey();

        products.MapGet("/", async (ProductDomain? domain, ProductVersionStatus? status, string? cursor, int? limit, IDispatcher dispatcher, CancellationToken ct) =>
            {
                if (!Cursor.TryDecodeKey(cursor, out var afterCode))
                {
                    return Problems.For(BuildingBlocks.Domain.ErrorCodes.ValidationFailed, "The cursor is not valid.", StatusCodes.Status422UnprocessableEntity);
                }

                var pageSize = Cursor.ClampLimit(limit);
                return (await dispatcher.Query(new ListProducts(domain, status, afterCode, pageSize), ct))
                    .ToHttp(items => Results.Ok(new CursorPage<ProductSummary>(items, items.Count == pageSize ? Cursor.EncodeKey(items[^1].ProductCode) : null)));
            })
            .Produces<CursorPage<ProductSummary>>(StatusCodes.Status200OK)
            .WithName("ProductListProducts")
            .WithSummary("List products")
            .WithDescription("The tenant's products by product code, each with its latest version and that version's status, and the version in force "
                + "(Published or Effective) if any. Filter by domain and by the latest version's status; cursor paging (GAP-023).")
            .RequirePermission(ProductPermissions.ProductRead);

        var versions = products.MapGroup("/{productCode}/versions");
        versions.MapPost("/", async (string productCode, CreateDraftVersionRequest r, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                (await dispatcher.Send(new CreateDraftVersion(productCode, r.EffectiveFrom), ct)).ToHttp(view =>
                {
                    Concurrency.SetETag(http, 1);
                    return Results.Created($"/api/v1/product-factory/products/{productCode}/versions/{view.Version}", view with { RowVersion = 1 });
                }))
            .Produces<ProductVersionView>(StatusCodes.Status201Created)
            .WithName("ProductCreateDraftVersion")
            .WithSummary("Create the next version of a product as a draft copy of its latest version")
            .WithDescription("A change to a published product is always a new version (PRD-003). Only one version of a product is in progress at a time.")
            .RequirePermission(ProductPermissions.ProductCreate)
            .RequireIdempotencyKey();

        versions.MapGet("/", async (string productCode, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Query(new ListProductVersions(productCode), ct)).ToHttp(Results.Ok))
            .Produces<IReadOnlyList<ProductVersionView>>(StatusCodes.Status200OK)
            .WithName("ProductListVersions")
            .WithSummary("List a product's versions, newest first")
            .RequirePermission(ProductPermissions.ProductRead);

        versions.MapGet("/{versionNumber:int}", async (string productCode, int versionNumber, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                (await dispatcher.Query(new GetProductVersion(productCode, versionNumber), ct)).ToHttp(view =>
                {
                    Concurrency.SetETag(http, view.RowVersion);
                    return Results.Ok(view);
                }))
            .Produces<ProductVersionView>(StatusCodes.Status200OK)
            .WithName("ProductGetVersion")
            .WithSummary("Get a product version")
            .RequirePermission(ProductPermissions.ProductRead);

        versions.MapPut("/{versionNumber:int}", (string productCode, int versionNumber, EditConfigurationRequest r, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                Versioned(http, expected => dispatcher.Send(new EditConfiguration(productCode, versionNumber, r.Name, r.Coverages, r.EffectiveFrom, expected, r.CoverStartsOn,
                    r.EligibilityRulesetCode, r.ReinstatementArrears), ct)))
            .Produces(StatusCodes.Status204NoContent)
            .RequireIfMatch()
            .WithName("ProductEditConfiguration")
            .WithSummary("Edit a draft or validated version (a validated version returns to draft)")
            .WithDescription("Submitted, approved and published versions are immutable (PRD-002).")
            .RequirePermission(ProductPermissions.ProductUpdate);

        versions.MapPost("/{versionNumber:int}/validate", (string productCode, int versionNumber, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                Versioned(http, expected => dispatcher.Send(new ValidateVersion(productCode, versionNumber, expected), ct)))
            .Produces(StatusCodes.Status204NoContent)
            .RequireIfMatch()
            .WithName("ProductValidateVersion")
            .WithSummary("Validate a draft: coverages in the library and of the product's domain, benefit dependencies, effective date (PRD-004, PRD-009)")
            .RequirePermission(ProductPermissions.ProductUpdate);

        versions.MapPost("/{versionNumber:int}/simulations", async (string productCode, int versionNumber, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new RunSimulation(productCode, versionNumber), ct))
                .ToHttp(view => Results.Created($"/api/v1/product-factory/products/{productCode}/versions/{versionNumber}/simulations", view)))
            .Produces<SimulationRunView>(StatusCodes.Status201Created)
            .WithName("ProductRunSimulation")
            .WithSummary("Run the rating test case library against a validated version (PRD-005)")
            .WithDescription("Each case is rated as a dry run as at the version's effective date and compared with its expected premium and tolerance.")
            .RequirePermission(ProductPermissions.ProductUpdate)
            .RequireIdempotencyKey();

        versions.MapGet("/{versionNumber:int}/simulations", async (string productCode, int versionNumber, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Query(new ListSimulationRuns(productCode, versionNumber), ct)).ToHttp(Results.Ok))
            .Produces<IReadOnlyList<SimulationRunView>>(StatusCodes.Status200OK)
            .WithName("ProductListSimulations")
            .WithSummary("List a version's simulation runs, newest first")
            .RequirePermission(ProductPermissions.ProductRead);

        versions.MapPost("/{versionNumber:int}/submit", (string productCode, int versionNumber, SubmitForApprovalRequest r, IDispatcher dispatcher, HttpContext http,
                CancellationToken ct) => Versioned(http, expected => dispatcher.Send(new SubmitForApproval(productCode, versionNumber, r.Reason, expected), ct)))
            .Produces(StatusCodes.Status204NoContent)
            .RequireIfMatch()
            .WithName("ProductSubmitForApproval")
            .WithSummary("Submit a validated version for publication approval")
            .WithDescription("Needs a passed simulation of this configuration unless the tenant's approval settings say otherwise. Workflow then asks a "
                + "checker other than the submitter, with PRODUCT_PUBLICATION authority (PRD-006); approval publishes the version, rejection returns it to draft.")
            .WithEventualEffects("the Workflow approval appears (GET /api/v1/workflow/approvals); after approval the version becomes Published and "
                + "Rating loads it.")
            .RequirePermission(ProductPermissions.ProductPublish);

        versions.MapPost("/{versionNumber:int}/withdraw", (string productCode, int versionNumber, WithdrawVersionRequest r, IDispatcher dispatcher, HttpContext http,
                CancellationToken ct) => Versioned(http, expected => dispatcher.Send(new WithdrawVersion(productCode, versionNumber, r.EffectiveTo, expected), ct)))
            .Produces(StatusCodes.Status204NoContent)
            .RequireIfMatch()
            .WithName("ProductWithdrawVersion")
            .WithSummary("Withdraw an effective version from a date")
            .RequirePermission(ProductPermissions.ProductPublish);

        versions.MapPost("/{versionNumber:int}/retire", (string productCode, int versionNumber, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                Versioned(http, expected => dispatcher.Send(new RetireVersion(productCode, versionNumber, expected), ct)))
            .Produces(StatusCodes.Status204NoContent)
            .RequireIfMatch()
            .WithName("ProductRetireVersion")
            .WithSummary("Retire an effective version")
            .RequirePermission(ProductPermissions.ProductPublish);
    }

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
