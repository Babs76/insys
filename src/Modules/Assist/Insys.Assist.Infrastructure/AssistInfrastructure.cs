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
using Insys.Assist.Domain;
using Insys.Assist.Infrastructure.Persistence;
using Insys.BuildingBlocks.Application.Ai;
using Insys.BuildingBlocks.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Insys.Assist.Infrastructure;

public static class AssistInfrastructure
{
    public static IServiceCollection AddAssistInfrastructure(this IServiceCollection services)
    {
        const string schema = AssistDbContext.SchemaName;
        services.AddModuleDbContext<AssistDbContext>(schema, "Assist");
        services.AddModuleHandlers<AssistDbContext>(typeof(GetAssistSettings).Assembly);
        services.AddScoped<IAssistRepository, AssistRepository>();
        services.AddScoped<IStarterPacks, StarterPacks>();
        // No model unless the deployment connects one (the Cloud adapter project registers Claude when Assist:Provider is claude).
        services.TryAddSingleton<IAiModelProvider, NoAiModelProvider>();
        services.AddScoped<AssistRunner>();
        services.AddScoped<Explainer>();
        // The prompt templates are configuration (AIA-074): the host doesn't start without one for each AI-2 feature.
        services.AddOptions<AssistOptions>().BindConfiguration("Assist")
            .Validate(o => o.PromptFor(AssistFeatures.ExplainRefusal) is not null && o.PromptFor(AssistFeatures.ExplainPremium) is not null,
                "Assist:Prompts needs a version and text for EXPLAIN_REFUSAL and EXPLAIN_PREMIUM.")
            .Validate(o => o.InferenceRegions.Length > 0, "Assist:InferenceRegions needs the regions a tenant may name, in the AI provider's terms.")
            .ValidateOnStart();
        return services;
    }
}
