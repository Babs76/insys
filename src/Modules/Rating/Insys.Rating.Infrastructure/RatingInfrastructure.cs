using Insys.BuildingBlocks.Infrastructure;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Rating.Infrastructure.Persistence;
using Insys.Rating.Infrastructure.StaticData;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Rating.Infrastructure;

public static class RatingInfrastructure
{
    public static IServiceCollection AddRatingInfrastructure(this IServiceCollection services)
    {
        services.AddModuleDbContext<RatingDbContext>(RatingDbContext.SchemaName, "Rating");
        services.AddStaticData<RatingDbContext>(RatingDbContext.SchemaName, RatingStaticData.Descriptors);
        return services;
    }
}
