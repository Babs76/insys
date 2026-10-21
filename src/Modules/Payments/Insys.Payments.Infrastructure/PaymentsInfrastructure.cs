using Insys.BuildingBlocks.Infrastructure;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Payments.Infrastructure.Persistence;
using Insys.Payments.Infrastructure.StaticData;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Payments.Infrastructure;

public static class PaymentsInfrastructure
{
    public static IServiceCollection AddPaymentsInfrastructure(this IServiceCollection services)
    {
        services.AddModuleDbContext<PaymentsDbContext>(PaymentsDbContext.SchemaName, "Payments");
        services.AddStaticData<PaymentsDbContext>(PaymentsDbContext.SchemaName, PaymentsStaticData.Descriptors);
        return services;
    }
}
