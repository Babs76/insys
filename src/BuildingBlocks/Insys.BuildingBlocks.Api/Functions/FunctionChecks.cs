/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Api
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
using Insys.BuildingBlocks.Api.StaticData;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.BuildingBlocks.Api.Functions;

/// <summary>
/// The function check of a request (ADR-026 §2, steps 1 to 5), before the endpoint's permission (step 6). It applies to a
/// user Identity resolved; a service, an anonymous caller and an endpoint about the caller have no function.
/// </summary>
public static class FunctionChecks
{
    /// <summary>The functions the endpoint needs (any one of them), or none.</summary>
    public static IReadOnlyList<string> CodesFor(HttpContext http)
    {
        var metadata = http.GetEndpoint()?.Metadata;
        if (metadata?.GetMetadata<RequiredFunctionMetadata>() is { } required)
        {
            return required.Codes;
        }

        // Static data approve and reject: one route for every table, so the function is the table's catalogue module's (ADR-009).
        if (metadata?.GetMetadata<StaticDataDecisionFunctionMetadata>() is not null
            && http.Request.RouteValues.TryGetValue("module", out var module) && http.Request.RouteValues.TryGetValue("entity", out var entity))
        {
            var descriptor = http.RequestServices.GetServices<ModuleStaticDataTables>().Where(m => m.ModuleRoute == module as string)
                .SelectMany(m => m.Descriptors).FirstOrDefault(d => d.EntityRoute == entity as string);
            return descriptor is null ? [] : [StaticDataFunctions.Approve(descriptor)];
        }

        return [];
    }

    /// <summary>The refusal of the request's function for the caller, or null when it may go on to the permission check.</summary>
    public static IResult? Refusal(HttpContext http)
    {
        var scope = http.RequestServices.GetRequiredService<ExecutionScope>();
        if (scope.Functions is not { } functions || CodesFor(http) is not { Count: > 0 } codes || functions.Refusal(codes) is not { } code)
        {
            return null;
        }

        return code switch
        {
            Domain.ErrorCodes.TenantNotActive => Problems.For(code, "The tenant is still onboarding: business transactions start once it is activated.",
                StatusCodes.Status409Conflict),
            Domain.ErrorCodes.FunctionDisabled => Problems.For(code, "The function is not available to the tenant.", StatusCodes.Status403Forbidden),
            Domain.ErrorCodes.FeatureNotEntitled => Problems.For(code, "The tenant is not entitled to the feature this function needs.", StatusCodes.Status403Forbidden),
            _ => Problems.For(code, "The caller's roles do not grant this function.", StatusCodes.Status403Forbidden),
        };
    }
}
