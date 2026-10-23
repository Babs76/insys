/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Rules
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
using Insys.Rules.Api.StaticData;
using Insys.Rules.Application;
using Insys.Rules.Contracts;
using Insys.Rules.Domain.Decisions;
using Insys.Rules.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Rules.Api;

/// <param name="Kind">Eligibility (a ruleset_code) or Underwriting (a product code: acceptance, referral and decline rules).</param>
public sealed record EvaluateRuleSetRequest(RuleSetKind Kind, string Code, string SubjectType, Guid SubjectId, JsonElement Inputs);

/// <summary>The Rules module: evaluates eligibility and underwriting rule sets and records each decision (SRS §7.4, LIF-002).</summary>
public sealed class RulesModule : IModule
{
    public string Name => "Rules";

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddRulesInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        RulesStaticDataEndpoints.MapStaticData(api);
        var decisions = api.MapGroup("/rules/decisions").WithTags(Name);

        decisions.MapPost("/", async (EvaluateRuleSetRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new EvaluateRuleSet(request.Kind, request.Code, request.SubjectType, request.SubjectId, request.Inputs), ct))
                .ToHttp(view => Results.Created($"/api/v1/rules/decisions/{view.Id}", view)))
            .Produces<RuleDecisionView>(StatusCodes.Status201Created)
            .WithName("RulesEvaluateRuleSet")
            .WithSummary("Evaluate a rule set and record the decision")
            .WithDescription("The most severe outcome among the rules that fire is the decision. A rule that cannot be evaluated, for example "
                + "because an input it reads is missing, fails the evaluation and nothing is recorded.")
            .RequirePermission(RulesPermissions.DecisionCreate)
            .RequireIdempotencyKey();

        decisions.MapGet("/{id:guid}", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Query(new GetRuleDecision(id), ct)).ToHttp(Results.Ok))
            .Produces<RuleDecisionView>(StatusCodes.Status200OK)
            .WithName("RulesGetDecision")
            .WithSummary("Get a recorded rule decision with its inputs")
            .RequirePermission(RulesPermissions.DecisionRead);

        decisions.MapGet("/", async (string subjectType, Guid subjectId, int? limit, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Query(new ListRuleDecisions(subjectType, subjectId, Cursor.ClampLimit(limit)), ct)).ToHttp(Results.Ok))
            .Produces<IReadOnlyList<RuleDecisionView>>(StatusCodes.Status200OK)
            .WithName("RulesListDecisions")
            .WithSummary("List a subject's rule decisions, newest first")
            .RequirePermission(RulesPermissions.DecisionRead);
    }
}
