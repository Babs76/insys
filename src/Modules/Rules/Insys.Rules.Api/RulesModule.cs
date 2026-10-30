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
/// <param name="Code">
/// The rule set to evaluate (BR-078). For a Life proposal's eligibility (kind ELIGIBILITY, subjectType LIFE_PROPOSAL), the
/// eligibilityRulesetCode of the proposal's product version: Life attaches no other rule set's result to a proposal. For a
/// Life underwriting case (kind UNDERWRITING, subjectType LIFE_UNDERWRITING_CASE), an underwriting rule set in force.
/// </param>
/// <param name="SubjectType">The record the decision is about (BR-077): Life attaches the result to it once rules.RuleDecisionRecorded is handled.</param>
public sealed record EvaluateRuleSetRequest(RuleSetKind Kind, string Code,
    [property: BuildingBlocks.Domain.KnownValues("LIFE_PROPOSAL", "LIFE_UNDERWRITING_CASE")] string SubjectType, Guid SubjectId, JsonElement Inputs);

/// <summary>The Rules module: evaluates eligibility and underwriting rule sets and records each decision (SRS §7.4, LIF-002).</summary>
public sealed class RulesModule : IModule
{
    public string Name => "Rules";

    public IReadOnlyList<BuildingBlocks.Api.Functions.FunctionDeclaration> Functions => RulesFunctions.All;

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
            .WithEventualEffects("for a LIFE_PROPOSAL, its eligibilityOutcome appears; for a LIFE_UNDERWRITING_CASE, its ruleOutcome appears, once Life handles "
                + "rules.RuleDecisionRecorded (BR-077).")
            .WithDescription("The most severe outcome among the rules that fire is the decision. A rule whose input is missing refers the decision "
                + "to a person (BR-113), listed in firedRules as \"RULE (no input name)\". A rule that cannot otherwise be evaluated, for example an input "
                + "of the wrong kind, fails the evaluation and nothing is recorded. An underwriting rule set (code: a product) has the decline conditions "
                + "of the product or of all products, and the acceptance and referral rules of the product, of its domain, or with no scope.")
            .RequirePermission(RulesPermissions.DecisionCreate)
            .RequireIdempotencyKey();

        decisions.MapGet("/{id:guid}", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Query(new GetRuleDecision(id), ct)).ToHttp(Results.Ok))
            .Produces<RuleDecisionView>(StatusCodes.Status200OK)
            .WithName("RulesGetDecision")
            .WithSummary("Get a recorded rule decision with its inputs")
            .RequirePermission(RulesPermissions.DecisionRead);

        decisions.MapGet("/", async (string subjectType, Guid subjectId, string? cursor, int? limit, IDispatcher dispatcher, CancellationToken ct) =>
            {
                if (!Cursor.TryDecode(cursor, out var before))
                {
                    return Problems.For(BuildingBlocks.Domain.ErrorCodes.ValidationFailed, "The cursor is not valid.", StatusCodes.Status422UnprocessableEntity);
                }

                var pageSize = Cursor.ClampLimit(limit);
                return (await dispatcher.Query(new ListRuleDecisions(subjectType, subjectId, before, pageSize), ct))
                    .ToHttp(items => Results.Ok(new CursorPage<RuleDecisionView>(items, items.Count == pageSize ? Cursor.Encode(items[^1].Id) : null)));
            })
            .Produces<CursorPage<RuleDecisionView>>(StatusCodes.Status200OK)
            .WithName("RulesListDecisions")
            .WithSummary("List a subject's rule decisions, newest first")
            .RequirePermission(RulesPermissions.DecisionRead);
    }
}
