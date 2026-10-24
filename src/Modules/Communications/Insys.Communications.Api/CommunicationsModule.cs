/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Communications
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
using Insys.Communications.Api.StaticData;
using Insys.Communications.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Communications.Api;

/// <summary>
/// The Communications module (addenda/communications.md, Wave 1: F021, F031, F078, F095–F102): notification types and
/// templates, channels behind adapters, preferences and consent, delivery tracking and provider callbacks.
/// </summary>
public sealed class CommunicationsModule : IModule
{
    public string Name => "Communications";

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddCommunicationsInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        CommunicationsStaticDataEndpoints.MapStaticData(api);
    }
}
