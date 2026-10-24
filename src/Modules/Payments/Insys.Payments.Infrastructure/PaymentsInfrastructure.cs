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
        services.AddScoped<PaymentScopes>();
        services.AddSingleton<Workflow.Contracts.IApprovalScopeSource, PayoutApprovalScopes>();
        services.AddScoped<ICallbackEndpoints, CallbackEndpoints>();
        services.AddSingleton<ICallbackEndpointResolver, CallbackEndpointResolver>();
        services.AddOptions<PaymentCallbackOptions>().BindConfiguration("Payments:Callbacks");
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

        // Onboarding readiness (addenda/onboarding-readiness.md §2): the configuration this module owns.
        BuildingBlocks.Infrastructure.Readiness.ReadinessRegistration.AddStaticReadiness(services, Persistence.PaymentsDbContext.SchemaName, PaymentsStaticData.Descriptors,
            new BuildingBlocks.Infrastructure.Readiness.StaticCheck("PAYMENTS_AND_DISTRIBUTION", "TENANT_BANK_ACCOUNT", "payment.mst_tenant_bank_accounts", "A tenant bank account"));
        BuildingBlocks.Infrastructure.Readiness.ReadinessRegistration.AddSqlReadiness(services, Persistence.PaymentsDbContext.SchemaName, ["PAYMENTS_AND_DISTRIBUTION"], async (context, connection, transaction, ct) =>
        {
            var reference = BuildingBlocks.Infrastructure.StaticData.StaticLifecycle.Reference.InForce;
            var currency = context.BaseCurrency ?? "";
            var directions = await BuildingBlocks.Infrastructure.Readiness.ReadinessSql.Strings(connection, transaction,
                $"SELECT DISTINCT direction FROM payment.ref_payment_methods WHERE (tenant_id = @t OR tenant_id IS NULL) AND {BuildingBlocks.Infrastructure.Readiness.ReadinessSql.InForce} AND @currency = ANY(currencies)",
                ct, ("t", context.TenantId), ("inForce", reference), ("day", context.Day), ("currency", currency));
            var methods = new List<string>();
            if (!directions.Contains("COLLECT") && !directions.Contains("BOTH")) methods.Add($"a COLLECT method in {(currency.Length > 0 ? currency : "the base currency")}");
            if (!directions.Contains("PAY") && !directions.Contains("BOTH")) methods.Add($"a PAY method in {(currency.Length > 0 ? currency : "the base currency")}");
            var providers = await BuildingBlocks.Infrastructure.Readiness.ReadinessSql.Count(connection, transaction,
                $"SELECT count(*) FROM payment.mst_payment_provider_and_bank_register WHERE tenant_id = @t AND {BuildingBlocks.Infrastructure.Readiness.ReadinessSql.InForce}", ct,
                ("t", context.TenantId), ("inForce", reference), ("day", context.Day));
            var mappings = await BuildingBlocks.Infrastructure.Readiness.ReadinessSql.Count(connection, transaction,
                $"SELECT count(*) FROM payment.ref_payment_status_mappings WHERE (tenant_id = @t OR tenant_id IS NULL) AND {BuildingBlocks.Infrastructure.Readiness.ReadinessSql.InForce}", ct,
                ("t", context.TenantId), ("inForce", reference), ("day", context.Day));
            var endpoints = await BuildingBlocks.Infrastructure.Readiness.ReadinessSql.Count(connection, transaction,
                "SELECT count(*) FROM payment.callback_endpoint WHERE tenant_id = @t AND status = 'ACTIVE'", ct, ("t", context.TenantId));
            var provider = new List<string>();
            if (providers == 0) provider.Add("payment.mst_payment_provider_and_bank_register");
            if (mappings == 0) provider.Add("payment.ref_payment_status_mappings");
            if (endpoints == 0) provider.Add("a callback endpoint (ADR-023)");
            return
            [
                BuildingBlocks.Application.ReadinessResult.Of("PAYMENTS_AND_DISTRIBUTION", "PAYMENT_METHODS", "A collect and a pay method in the base currency", methods),
                BuildingBlocks.Application.ReadinessResult.Of("PAYMENTS_AND_DISTRIBUTION", "PAYMENT_PROVIDER",
                    "A payment provider with status mappings, and a callback endpoint issued", provider),
            ];
        });
        return services;
    }
}
