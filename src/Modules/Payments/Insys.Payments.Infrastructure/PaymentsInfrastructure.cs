using Insys.Billing.Contracts;
using Insys.BuildingBlocks.Infrastructure;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.ClaimsFramework.Contracts;
using Insys.Payments.Application;
using Insys.Payments.Contracts;
using Insys.Payments.Infrastructure.Persistence;
using Insys.Payments.Infrastructure.StaticData;
using Insys.Workflow.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Payments.Infrastructure;

public static class PaymentsInfrastructure
{
    public static IServiceCollection AddPaymentsInfrastructure(this IServiceCollection services)
    {
        const string schema = PaymentsDbContext.SchemaName;
        services.AddModuleDbContext<PaymentsDbContext>(schema, "Payments");
        services.AddStaticData<PaymentsDbContext>(schema, PaymentsStaticData.Descriptors);
        services.AddModuleHandlers<PaymentsDbContext>(typeof(InitiatePayment).Assembly);
        services.AddDomainEventMapper<PaymentsEventMapper>(schema);
        services.AddScoped<IPaymentRepository, PaymentRepository>();
        services.AddScoped<IPaymentCodes, PaymentCodes>();
        services.AddScoped<PaymentStarter>();
        services.AddSingleton<IPaymentGateway, SimulatorGateway>();
        services.AddIntegrationEventHandler<InvoiceIssued, PaymentPolicies, PaymentsDbContext>(schema, "InvoiceIssued");
        services.AddIntegrationEventHandler<RefundApproved, PaymentPolicies, PaymentsDbContext>(schema, "RefundApproved");
        services.AddIntegrationEventHandler<ClaimPaymentInstructed, PaymentPolicies, PaymentsDbContext>(schema, "ClaimPaymentInstructed");
        services.AddIntegrationEventHandler<Health.Contracts.ProviderPaymentRunReleased, PaymentPolicies, PaymentsDbContext>(schema, "ProviderPaymentRunReleased");
        services.AddIntegrationEventHandler<ApprovalGranted, PaymentPolicies, PaymentsDbContext>(schema, "ApprovalGranted");
        services.AddIntegrationEventHandler<PaymentReceived, PaymentPolicies, PaymentsDbContext>(schema, "PaymentReceived");
        services.AddIntegrationEventHandler<PaymentSucceeded, PaymentPolicies, PaymentsDbContext>(schema, "PaymentSucceeded");
        services.AddIntegrationEventHandler<PaymentFailed, PaymentPolicies, PaymentsDbContext>(schema, "PaymentFailed");
        services.AddIntegrationEventHandler<PaymentOutcomeUnknown, PaymentPolicies, PaymentsDbContext>(schema, "PaymentOutcomeUnknown");
        services.AddOptions<PaymentJobOptions>().BindConfiguration("Payments:Jobs");
        services.AddSingleton<PaymentJobs>();
        if (!InfrastructureRegistration.IsBuildTimeDocumentGeneration)
        {
            services.AddHostedService<PaymentJobsService>();
        }

        return services;
    }
}
