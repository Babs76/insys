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
using Insys.BuildingBlocks.Infrastructure;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Rules.Application;
using Insys.Rules.Infrastructure.Persistence;
using Insys.Rules.Infrastructure.StaticData;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Rules.Infrastructure;

public static class RulesInfrastructure
{
    public static IServiceCollection AddRulesInfrastructure(this IServiceCollection services)
    {
        services.AddModuleDbContext<RulesDbContext>(RulesDbContext.SchemaName, "Rules");
        services.AddStaticData<RulesDbContext>(RulesDbContext.SchemaName, RulesStaticData.Descriptors);
        services.AddModuleHandlers<RulesDbContext>(typeof(EvaluateRuleSet).Assembly);
        services.AddDomainEventMapper<RulesEventMapper>(RulesDbContext.SchemaName);
        services.AddScoped<IRuleSetReader, RuleSetReader>();
        services.AddScoped<IRuleDecisionRepository, RuleDecisionRepository>();
        services.AddScoped<IRuleDecisionQueries, RuleDecisionQueries>();
        services.AddSingleton<Contracts.IUnderwritingAuthorities, UnderwritingAuthorities>();
        return services;
    }
}
