/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Payments
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
        services.AddScoped<ICallbackEndpoints, CallbackEndpoints>();
        services.AddSingleton<ICallbackEndpointResolver, CallbackEndpointResolver>();
        services.AddOptions<CallbackOptions>().BindConfiguration("Payments:Callbacks");
        services.AddScoped<IPaymentCodes, PaymentCodes>();
        services.AddScoped<PaymentStarter>();
        services.AddSingleton<IPaymentGateway, SimulatorGateway>();
        services.AddIntegrationEventHandler<InvoiceIssued, PaymentPolicies, PaymentsDbContext>(schema, "InvoiceIssued");
        services.AddIntegrationEventHandler<RefundApproved, PaymentPolicies, PaymentsDbContext>(schema, "RefundApproved");
        services.AddIntegrationEventHandler<ClaimPaymentInstructed, PaymentPolicies, PaymentsDbContext>(schema, "ClaimPaymentInstructed");
        services.AddIntegrationEventHandler<Health.Contracts.ProviderPaymentRunReleased, PaymentPolicies, PaymentsDbContext>(schema, "ProviderPaymentRunReleased");
        services.AddIntegrationEventHandler<Distribution.Contracts.CommissionStatementApproved, PaymentPolicies, PaymentsDbContext>(schema, "CommissionStatementApproved");
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
