using Insys.BuildingBlocks.Infrastructure;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.ClaimsFramework.Application;
using Insys.ClaimsFramework.Contracts;
using Insys.General.Contracts;
using Insys.ClaimsFramework.Infrastructure.Persistence;
using Insys.ClaimsFramework.Infrastructure.StaticData;
using Insys.Payments.Contracts;
using Insys.Workflow.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.ClaimsFramework.Infrastructure;

public static class ClaimsFrameworkInfrastructure
{
    public static IServiceCollection AddClaimsFrameworkInfrastructure(this IServiceCollection services)
    {
        const string schema = ClaimsFrameworkDbContext.SchemaName;
        services.AddModuleDbContext<ClaimsFrameworkDbContext>(schema, "ClaimsFramework");
        services.AddStaticData<ClaimsFrameworkDbContext>(schema, ClaimsFrameworkStaticData.Descriptors);
        services.AddModuleHandlers<ClaimsFrameworkDbContext>(typeof(AllocateClaimNumber).Assembly);
        services.AddDomainEventMapper<ClaimsEventMapper>(schema);
        services.AddScoped<IClaimsRepository, ClaimsRepository>();
        services.AddScoped<IClaimsCodes, ClaimsCodes>();
        services.AddIntegrationEventHandler<ApprovalGranted, ClaimsPolicies, ClaimsFrameworkDbContext>(schema, "ApprovalGranted");
        services.AddIntegrationEventHandler<ApprovalRejected, ClaimsPolicies, ClaimsFrameworkDbContext>(schema, "ApprovalRejected");
        services.AddIntegrationEventHandler<PaymentSucceeded, ClaimsPolicies, ClaimsFrameworkDbContext>(schema, "PaymentSucceeded");
        services.AddIntegrationEventHandler<PaymentFailed, ClaimsPolicies, ClaimsFrameworkDbContext>(schema, "PaymentFailed");
        services.AddIntegrationEventHandler<ClaimPaymentCompleted, ClaimsPolicies, ClaimsFrameworkDbContext>(schema, "ClaimPaymentCompleted");
        services.AddIntegrationEventHandler<GeneralClaimApproved, ClaimsPolicies, ClaimsFrameworkDbContext>(schema, "GeneralClaimApproved");
        services.AddIntegrationEventHandler<RecoveryRecorded, ClaimsPolicies, ClaimsFrameworkDbContext>(schema, "RecoveryRecorded");
        services.AddScoped<IClaimRegistry, ClaimRegistry>();
        return services;
    }
}
