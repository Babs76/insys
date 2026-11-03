/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Api.Host
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
using Insys.BuildingBlocks.Application;
using Insys.BuildingBlocks.Application.Context;

namespace Insys.Api.Platform;

/// <summary>Where the caller is: the deployment's environment, whether it's production, and the integrations answered by stubs.</summary>
/// <param name="Environment">ASPNETCORE_ENVIRONMENT, such as Preview.</param>
/// <param name="NonProduction">True everywhere but Production: the console shows a permanent banner.</param>
/// <param name="Stubs">Each integration a stub answers, so nothing reaches a real provider.</param>
public sealed record EnvironmentView(string Environment, bool NonProduction, IReadOnlyList<StubInUse> Stubs);

public static class EnvironmentInfo
{
    /// <summary>GET /api/v1/platform/environment, for the console's banner and its list of stubs (the INSYS Preview's labels).</summary>
    public static void MapEnvironmentInfo(this IEndpointRouteBuilder v1, IHostEnvironment environment) =>
        v1.MapGet("/platform/environment", (IEnumerable<StubInUse> stubs) =>
                Results.Ok(new EnvironmentView(environment.EnvironmentName, !environment.IsProduction(), [.. stubs.OrderBy(s => s.Area, StringComparer.Ordinal)])))
            .WithTags("Platform")
            .WithName("PlatformGetEnvironment")
            .WithSummary("The deployment's environment, whether it is production, and the integrations stubs answer: for the console's banner and labels")
            .Produces<EnvironmentView>(StatusCodes.Status200OK)
            // Any signed-in user, staff or portal: nothing in it is about a tenant or a person.
            .RequireSignedInUser()
            .ForPortal(ExternalSubject.Party, ExternalSubject.Intermediary, ExternalSubject.Provider);
}
