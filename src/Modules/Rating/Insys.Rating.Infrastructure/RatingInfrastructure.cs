using Insys.BuildingBlocks.Contracts.StaticData;
using Insys.BuildingBlocks.Infrastructure;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.ProductFactory.Contracts;
using Insys.Rating.Application;
using Insys.Rating.Contracts;
using Insys.Rating.Infrastructure.Persistence;
using Insys.Rating.Infrastructure.StaticData;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Rating.Infrastructure;

public static class RatingInfrastructure
{
    public static IServiceCollection AddRatingInfrastructure(this IServiceCollection services)
    {
        const string schema = RatingDbContext.SchemaName;
        services.AddModuleDbContext<RatingDbContext>(schema, "Rating");
        services.AddStaticData<RatingDbContext>(schema, RatingStaticData.Descriptors);
        services.AddModuleHandlers<RatingDbContext>(typeof(CalculatePremium).Assembly);
        services.AddDomainEventMapper<RatingEventMapper>(schema);
        services.AddMemoryCache();
        services.AddSingleton<RatingConfigurationCache>();
        services.AddScoped<IRatingConfigurationReader, RatingConfigurationReader>();
        services.AddScoped<IRatingTestCases, RatingTestCases>();
        services.AddScoped<IPremiumCalculationRepository, PremiumCalculationRepository>();
        services.AddScoped<IPremiumCalculationQueries, PremiumCalculationQueries>();
        services.AddScoped<PremiumRater>();
        services.AddScoped<IPremiumSimulator, PremiumSimulator>();
        services.AddIntegrationEventHandler<ProductVersionPublished, RatingCachePolicies, RatingDbContext>(schema, "ProductVersionPublished");
        services.AddIntegrationEventHandler<StaticDataVersionActivated, RatingCachePolicies, RatingDbContext>(schema, "StaticDataActivated");
        return services;
    }
}
