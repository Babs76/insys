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
                if (view.RowVersion is { } version)
                {
                    Concurrency.SetETag(http, version);
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
                        Concurrency.SetETag(http, view.RowVersion ?? 1);
                        return Results.Ok(view);
                    })
                    : Results.BadRequest())
            .Produces<AssistSettingsView>(StatusCodes.Status200OK)
            .ReturnsETag()
            .WithName("AssistSetSettings").WithSummary("Set them. If-Match is needed once they exist")
            .RequirePermission(AssistPermissions.SettingsManage);
    }
}
