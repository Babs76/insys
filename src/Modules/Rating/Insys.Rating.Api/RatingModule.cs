/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Rating
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
using System.Text.Json;
using Insys.BuildingBlocks.Api;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.Rating.Api.StaticData;
using Insys.Rating.Application;
using Insys.Rating.Contracts;
using Insys.Rating.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Rating.Api;

/// <param name="RequestRef">The caller's reference for the calculation (up to 30 characters).</param>
/// <param name="SourceDomain">LIFE, GENERAL, HEALTH, or PRODUCT for a simulation.</param>
public sealed record CalculatePremiumRequest(string RequestRef, string SourceDomain, string SubjectType, Guid SubjectId, string ProductCode, int ProductVersion,
    string Currency, string Frequency, DateOnly? RatingDate, JsonElement Values);

public sealed record SimulatePremiumRequest(string ProductCode, int ProductVersion, string Currency, string Frequency, DateOnly? RatingDate, JsonElement Values);

/// <summary>The Rating module: premiums from rating factors, tables and parameters, each calculation recorded with its versions (SRS §7.3).</summary>
public sealed class RatingModule : IModule
{
    public string Name => "Rating";

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddRatingInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        RatingStaticDataEndpoints.MapStaticData(api);
        var calculations = api.MapGroup("/rating/calculations").WithTags(Name);

        calculations.MapPost("/", async (CalculatePremiumRequest r, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new CalculatePremium(r.RequestRef, r.SourceDomain, r.SubjectType, r.SubjectId,
                    new RatingRequest(r.ProductCode, r.ProductVersion, r.Currency, r.Frequency, r.RatingDate, r.Values)), ct))
                .ToHttp(view => Results.Created($"/api/v1/rating/calculations/{view.Id}", view)))
            .Produces<CalculationView>(StatusCodes.Status201Created)
            .WithName("RatingCalculatePremium")
            .WithSummary("Calculate a premium and record it with its breakdown")
            .WithDescription("Uses the rating configuration in force on the rating date (today by default). The record keeps the inputs, the product "
                + "version and the rating version, so recalculating as at the same date gives the same premium (PRD-008, LIF-002).")
            .RequirePermission(RatingPermissions.CalculationCreate)
            .RequireIdempotencyKey();

        calculations.MapPost("/simulate", async (SimulatePremiumRequest r, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Query(new SimulatePremium(new RatingRequest(r.ProductCode, r.ProductVersion, r.Currency, r.Frequency, r.RatingDate, r.Values)), ct))
                .ToHttp(Results.Ok))
            .Produces<SimulationView>(StatusCodes.Status200OK)
            .WithName("RatingSimulatePremium")
            .WithSummary("Rate without recording anything")
            .RequirePermission(RatingPermissions.Simulate);

        calculations.MapGet("/{id:guid}", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Query(new GetCalculation(id), ct)).ToHttp(Results.Ok))
            .Produces<CalculationView>(StatusCodes.Status200OK)
            .WithName("RatingGetCalculation")
            .WithSummary("Get a recorded calculation with its breakdown")
            .RequirePermission(RatingPermissions.CalculationRead);

        calculations.MapGet("/", async (string subjectType, Guid subjectId, int? limit, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Query(new ListCalculations(subjectType, subjectId, Cursor.ClampLimit(limit)), ct)).ToHttp(Results.Ok))
            .Produces<IReadOnlyList<CalculationView>>(StatusCodes.Status200OK)
            .WithName("RatingListCalculations")
            .WithSummary("List a subject's calculations, newest first")
            .RequirePermission(RatingPermissions.CalculationRead);
    }
}
