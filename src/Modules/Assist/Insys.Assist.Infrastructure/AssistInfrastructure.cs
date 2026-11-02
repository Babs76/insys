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
using Insys.Assist.Infrastructure.Persistence;
using Insys.BuildingBlocks.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Assist.Infrastructure;

public static class AssistInfrastructure
{
    public static IServiceCollection AddAssistInfrastructure(this IServiceCollection services)
    {
        const string schema = AssistDbContext.SchemaName;
        services.AddModuleDbContext<AssistDbContext>(schema, "Assist");
        services.AddModuleHandlers<AssistDbContext>(typeof(GetAssistSettings).Assembly);
        services.AddScoped<IAssistRepository, AssistRepository>();
        services.AddSingleton<IAiModelProvider, StubAiModelProvider>();
        return services;
    }
}
