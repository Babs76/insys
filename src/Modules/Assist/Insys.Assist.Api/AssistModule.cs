/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Assist
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
using Insys.Assist.Application;
using Insys.Assist.Contracts;
using Insys.Assist.Infrastructure;
using Insys.BuildingBlocks.Api;
using Insys.BuildingBlocks.Application.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Assist.Api;

public sealed record AssistSettingsRequest(string Jurisdiction, string InferenceRegion, string? LegalBasis, bool ExplanationsEnabled);

/// <param name="Code">The refusal's error code.</param>
/// <param name="Fields">The names of the fields in error, as the refusal's field errors named them; their messages aren't sent (AIA-039, D-15).</param>
/// <param name="RecordType">The record the refusal was about, by type and ID, for the "Based on" link and the audit; never trusted for its data (AIA-039).</param>
public sealed record ExplainRefusalBody(string Code, IReadOnlyList<string>? Fields, string? RecordType, Guid? RecordId, Guid? CorrelationId);

public sealed record ExplainPremiumBody(Guid CalculationId, Guid? CorrelationId);

/// <param name="Reason">WRONG, UNCLEAR, INCOMPLETE or OTHER; needed when the explanation isn't helpful.</param>
/// <param name="Comment">At most 500 characters, kept for evaluation and never sent to a model.</param>
public sealed record ExplanationFeedbackBody(bool Helpful, string? Reason, string? Comment);

/// <summary>AI Assist (Wave 7, docs/specs/addenda/ai-assist.md): the AI prepares and a person decides. INSYS works unchanged without it.</summary>
public sealed class AssistModule : IModule
{
    public string Name => "Assist";

    public IReadOnlyList<BuildingBlocks.Api.Functions.FunctionDeclaration> Functions => AssistFunctions.All;

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddAssistInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var assist = api.MapGroup("/assist").WithTags(Name);
        assist.MapGet("/settings", async (IDispatcher d, HttpContext http, CancellationToken ct) => (await d.Query(new GetAssistSettings(), ct)).ToHttp(view =>
            {
                if (view.RowVersion > 0)
                {
                    Concurrency.SetETag(http, view.RowVersion);
                }

                return Results.Ok(view);
            }))
            .Produces<AssistSettingsView>(StatusCodes.Status200OK)
            .ReturnsETag()
            .WithName("AssistGetSettings").WithSummary("The tenant's AI Assist settings: jurisdiction, where inference runs, legal basis (AIA-050)")
            .RequirePermission(AssistPermissions.SettingsManage);
        assist.MapGet("/usage", async (string? month, IDispatcher d, CancellationToken ct) => (await d.Query(new GetAssistUsage(month), ct)).ToHttp(Results.Ok))
            .Produces<AssistUsageView>(StatusCodes.Status200OK)
            .WithName("AssistGetUsage").WithSummary("A month's use against the cap: tokens and runs (addendum §6). Billing is outside INSYS")
            .RequirePermission(AssistPermissions.SettingsManage);
        assist.MapPut("/settings", async (AssistSettingsRequest r, IDispatcher d, HttpContext http, CancellationToken ct) =>
                Concurrency.OptionalExpectedVersion(http.Request) is { IsSuccess: true } expected
                    ? (await d.Send(new SetAssistSettings(r.Jurisdiction, r.InferenceRegion, r.LegalBasis, r.ExplanationsEnabled, expected.Value), ct)).ToHttp(view =>
                    {
                        Concurrency.SetETag(http, view.RowVersion);
                        return Results.Ok(view);
                    })
                    : Results.BadRequest())
            .Produces<AssistSettingsView>(StatusCodes.Status200OK)
            .ReturnsETag()
            .WithName("AssistSetSettings").WithSummary("Set them. If-Match is needed once they exist")
            .RequirePermission(AssistPermissions.SettingsManage);

        var explanations = assist.MapGroup("/explanations");
        explanations.MapPost("/refusals", async (ExplainRefusalBody b, Explainer explainer, CancellationToken ct) =>
                (await explainer.Refusal(new ExplainRefusalRequest(b.Code, b.Fields, b.RecordType, b.RecordId, b.CorrelationId), ct)).ToHttp(Results.Ok))
            .Produces<ExplanationView>(StatusCodes.Status200OK)
            .WithName("AssistExplainRefusal")
            .WithSummary("Explain a refusal the caller received (AIA-039): its code, the names of its fields in error, the record and the correlation ID")
            .WithDescription("Built from the error catalogue's wording for the code; the screen's messages and record data are never used. When no explanation can be shown, outcome says why and facts hold the plain refusal (AIA-033, P8).")
            .RequirePermission(AssistPermissions.ExplainRefusal)
            .RequireIdempotencyKey();
        explanations.MapPost("/premiums", async (ExplainPremiumBody b, Explainer explainer, CancellationToken ct) =>
                (await explainer.Premium(new ExplainPremiumRequest(b.CalculationId, b.CorrelationId), ct)).ToHttp(Results.Ok))
            .Produces<ExplanationView>(StatusCodes.Status200OK)
            .WithName("AssistExplainPremium")
            .WithSummary("Explain a recorded premium calculation (AIA-032), which the caller must be able to read")
            .WithDescription("Built only from the recorded calculation's inputs and lines, every amount with its currency. When no explanation can be shown, facts hold the plain breakdown (AIA-033, P8).")
            .RequirePermission(AssistPermissions.ExplainPremium)
            .RequireIdempotencyKey();
        explanations.MapPost("/{id:guid}/feedback", async (Guid id, ExplanationFeedbackBody b, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new GiveExplanationFeedback(id, b.Helpful, b.Reason, b.Comment), ct)).ToHttp(feedback => Results.Created($"/api/v1/assist/explanations/{id}/feedback/{feedback}", new { id = feedback })))
            .Produces(StatusCodes.Status201Created)
            .WithName("AssistGiveExplanationFeedback")
            .WithSummary("Mark an explanation you were shown helpful, or unhelpful or wrong with a reason (AIA-037)")
            .WithDescription("Only the user an explanation was shown to marks it, so the route needs no function of its own: anyone else gets 404.")
            .RequireSignedInUser()
            .RequireIdempotencyKey();
    }
}
