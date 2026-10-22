using Insys.Billing.Contracts;
using Insys.BuildingBlocks.Infrastructure;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.ClaimsFramework.Contracts;
using Insys.Finance.Application;
using Insys.Finance.Infrastructure.Persistence;
using Insys.Finance.Infrastructure.StaticData;
using Insys.Payments.Contracts;
using Insys.Workflow.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Finance.Infrastructure;

public static class FinanceInfrastructure
{
    public static IServiceCollection AddFinanceInfrastructure(this IServiceCollection services)
    {
        const string schema = FinanceDbContext.SchemaName;
        services.AddModuleDbContext<FinanceDbContext>(schema, "Finance");
        services.AddStaticData<FinanceDbContext>(schema, FinanceStaticData.Descriptors);
        services.AddModuleHandlers<FinanceDbContext>(typeof(ReverseJournal).Assembly);
        services.AddDomainEventMapper<FinanceEventMapper>(schema);
        services.AddScoped<IJournalRepository, JournalRepository>();
        services.AddScoped<IAccountingPeriods, AccountingPeriods>();
        services.AddScoped<IPostingExceptions, PostingExceptions>();
        services.AddScoped<IPostingConfiguration, PostingConfiguration>();
        services.AddScoped<PostingService>();
        services.AddScoped<EventPoster>();

        services.AddIntegrationEventHandler<InvoiceIssued, PostingPolicies, FinanceDbContext>(schema, "InvoiceIssued");
        services.AddIntegrationEventHandler<CreditNoteIssued, PostingPolicies, FinanceDbContext>(schema, "CreditNoteIssued");
        services.AddIntegrationEventHandler<PaymentReceived, PostingPolicies, FinanceDbContext>(schema, "PaymentReceived");
        services.AddIntegrationEventHandler<PaymentSucceeded, PostingPolicies, FinanceDbContext>(schema, "PaymentSucceeded");
        services.AddIntegrationEventHandler<PaymentReversed, PostingPolicies, FinanceDbContext>(schema, "PaymentReversed");
        services.AddIntegrationEventHandler<ClaimReserveChanged, PostingPolicies, FinanceDbContext>(schema, "ClaimReserveChanged");
        services.AddIntegrationEventHandler<ClaimPaymentCompleted, PostingPolicies, FinanceDbContext>(schema, "ClaimPaymentCompleted");
        services.AddIntegrationEventHandler<ClaimRecoveryRecorded, PostingPolicies, FinanceDbContext>(schema, "ClaimRecoveryRecorded");
        services.AddIntegrationEventHandler<ApprovalGranted, PeriodReopenPolicy, FinanceDbContext>(schema, "ApprovalGranted");
        services.AddIntegrationEventHandler<ApprovalRejected, PeriodReopenPolicy, FinanceDbContext>(schema, "ApprovalRejected");
        return services;
    }
}
