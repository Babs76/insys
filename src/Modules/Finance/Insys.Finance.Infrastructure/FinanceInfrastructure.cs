/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Finance
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
using Insys.Billing.Contracts;
using Insys.BuildingBlocks.Infrastructure;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.ClaimsFramework.Contracts;
using Insys.Finance.Application;
using Insys.Finance.Contracts;
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
        services.AddScoped<IGlExports, GlExports>();
        services.AddScoped<IPostingConfiguration, PostingConfiguration>();
        services.AddScoped<PostingService>();
        services.AddScoped<EventPoster>();
        services.AddSingleton<ILedgerBalances, LedgerBalances>();

        services.AddIntegrationEventHandler<InvoiceIssued, PostingPolicies, FinanceDbContext>(schema, "InvoiceIssued");
        services.AddIntegrationEventHandler<CreditNoteIssued, PostingPolicies, FinanceDbContext>(schema, "CreditNoteIssued");
        services.AddIntegrationEventHandler<PaymentReceived, PostingPolicies, FinanceDbContext>(schema, "PaymentReceived");
        services.AddIntegrationEventHandler<PaymentSucceeded, PostingPolicies, FinanceDbContext>(schema, "PaymentSucceeded");
        services.AddIntegrationEventHandler<PaymentReversed, PostingPolicies, FinanceDbContext>(schema, "PaymentReversed");
        services.AddIntegrationEventHandler<ClaimReserveChanged, PostingPolicies, FinanceDbContext>(schema, "ClaimReserveChanged");
        services.AddIntegrationEventHandler<ClaimPaymentCompleted, PostingPolicies, FinanceDbContext>(schema, "ClaimPaymentCompleted");
        services.AddIntegrationEventHandler<Health.Contracts.MedicalClaimSettled, PostingPolicies, FinanceDbContext>(schema, "MedicalClaimSettled");
        services.AddIntegrationEventHandler<ClaimRecoveryRecorded, PostingPolicies, FinanceDbContext>(schema, "ClaimRecoveryRecorded");
        services.AddIntegrationEventHandler<Distribution.Contracts.CommissionAccrued, PostingPolicies, FinanceDbContext>(schema, "CommissionAccrued");
        services.AddIntegrationEventHandler<Distribution.Contracts.CommissionClawedBack, PostingPolicies, FinanceDbContext>(schema, "CommissionClawedBack");
        services.AddIntegrationEventHandler<Reinsurance.Contracts.CessionCalculated, PostingPolicies, FinanceDbContext>(schema, "CessionCalculated");
        services.AddIntegrationEventHandler<Reinsurance.Contracts.RecoveryBilled, PostingPolicies, FinanceDbContext>(schema, "RecoveryBilled");
        services.AddIntegrationEventHandler<Reinsurance.Contracts.RecoverySettled, PostingPolicies, FinanceDbContext>(schema, "RecoverySettled");
        services.AddIntegrationEventHandler<ApprovalGranted, PeriodReopenPolicy, FinanceDbContext>(schema, "ApprovalGranted");
        services.AddIntegrationEventHandler<ApprovalRejected, PeriodReopenPolicy, FinanceDbContext>(schema, "ApprovalRejected");
        // Onboarding readiness (addenda/onboarding-readiness.md §2): the configuration this module owns.
        BuildingBlocks.Infrastructure.Readiness.ReadinessRegistration.AddStaticReadiness(services, Persistence.FinanceDbContext.SchemaName, FinanceStaticData.Descriptors,
            new BuildingBlocks.Infrastructure.Readiness.StaticCheck("FINANCIAL_FOUNDATIONS", "CHART_OF_ACCOUNTS", "finance.mst_chart_of_accounts", "At least one account in the chart of accounts"),
            new BuildingBlocks.Infrastructure.Readiness.StaticCheck("INTEGRATION_AND_REPORTING", "GL_EXPORT_LAYOUT", "finance.cfg_gl_export_layouts", "A GL export layout (F055)"));
        BuildingBlocks.Infrastructure.Readiness.ReadinessRegistration.AddSqlReadiness(services, Persistence.FinanceDbContext.SchemaName, async (context, connection, transaction, ct) =>
        {
            // The transaction types every tenant's business raises (onboarding readiness §2, "only those used", proposed).
            string[] used = [Contracts.TransactionTypes.PremiumDue, Contracts.TransactionTypes.PaymentReceived, Contracts.TransactionTypes.RefundPaid,
                Contracts.TransactionTypes.ClaimReserveIncrease, Contracts.TransactionTypes.ClaimReserveDecrease, Contracts.TransactionTypes.ClaimPaid];
            var ruled = await BuildingBlocks.Infrastructure.Readiness.ReadinessSql.Strings(connection, transaction,
                $"SELECT DISTINCT transaction_type FROM finance.cfg_posting_rules WHERE tenant_id = @t AND {BuildingBlocks.Infrastructure.Readiness.ReadinessSql.InForce}", ct,
                ("t", context.TenantId), ("inForce", BuildingBlocks.Infrastructure.StaticData.StaticLifecycle.Configuration.InForce), ("day", context.Day));
            var open = await BuildingBlocks.Infrastructure.Readiness.ReadinessSql.Count(connection, transaction,
                "SELECT count(*) FROM finance.accounting_period WHERE tenant_id = @t AND status = 'OPEN' AND start_date <= @day AND end_date >= @day", ct,
                ("t", context.TenantId), ("day", context.Day));
            return
            [
                BuildingBlocks.Application.ReadinessResult.Of("FINANCIAL_FOUNDATIONS", "POSTING_RULES",
                    "A posting rule for premium due, payment received, refund paid, claim reserve up and down, and claim paid", used.Except(ruled).ToList()),
                BuildingBlocks.Application.ReadinessResult.Of("FINANCIAL_FOUNDATIONS", "OPEN_PERIOD", "An accounting period open on the business date",
                    open > 0 ? [] : [$"finance.accounting_period open on {context.Day:yyyy-MM-dd}"]),
            ];
        });
        return services;
    }
}
