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
        return services;
    }
}
