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
        return services;
    }
}
