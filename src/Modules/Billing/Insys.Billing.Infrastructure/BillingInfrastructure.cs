/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Billing
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
using Insys.Billing.Application;
using Insys.Billing.Contracts;
using Insys.Billing.Infrastructure.Persistence;
using Insys.Billing.Infrastructure.StaticData;
using Insys.BuildingBlocks.Infrastructure;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.General.Contracts;
using Insys.Life.Contracts;
using Insys.Payments.Contracts;
using Insys.Workflow.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Billing.Infrastructure;

public static class BillingInfrastructure
{
    public static IServiceCollection AddBillingInfrastructure(this IServiceCollection services)
    {
        const string schema = BillingDbContext.SchemaName;
        services.AddModuleDbContext<BillingDbContext>(schema, "Billing");
        services.AddStaticData<BillingDbContext>(schema, BillingStaticData.Descriptors);
        services.AddModuleHandlers<BillingDbContext>(typeof(OpenAccount).Assembly);
        services.AddDomainEventMapper<BillingEventMapper>(schema);
        services.AddScoped<IBillingRepository, BillingRepository>();
        services.AddSingleton<BuildingBlocks.Contracts.IBusinessRecordScopes, BillingRecordScopes>();
        services.AddScoped<IBillingCodes, BillingCodes>();
        services.AddSingleton<IAllocationPlanner, AllocationPlanner>();
        services.AddSingleton<IPolicyArrears, PolicyArrearsQuery>();
        services.AddIntegrationEventHandler<Contracts.InvoiceOverdue, ArrearsOnOverdue, BillingDbContext>(schema, "InvoiceOverdue");
        services.AddIntegrationEventHandler<Contracts.ArrearsAgeingBlocked, ArrearsOnOverdue, BillingDbContext>(schema, "ArrearsAgeingBlocked");
        services.AddIntegrationEventHandler<PaymentAllocated, BillingPolicies, BillingDbContext>(schema, "PaymentAllocated");
        services.AddIntegrationEventHandler<PaymentReversed, BillingPolicies, BillingDbContext>(schema, "PaymentReversed");
        services.AddIntegrationEventHandler<PaymentSucceeded, BillingPolicies, BillingDbContext>(schema, "PaymentSucceeded");
        services.AddIntegrationEventHandler<PaymentInitiated, BillingPolicies, BillingDbContext>(schema, "PaymentInitiated");
        services.AddIntegrationEventHandler<ApprovalGranted, BillingPolicies, BillingDbContext>(schema, "ApprovalGranted");
        services.AddIntegrationEventHandler<ApprovalRejected, BillingPolicies, BillingDbContext>(schema, "ApprovalRejected");
        services.AddIntegrationEventHandler<GeneralPolicyIssued, GeneralPolicyBilling, BillingDbContext>(schema, "GeneralPolicyIssued");
        services.AddIntegrationEventHandler<GeneralPolicyEndorsed, GeneralPolicyBilling, BillingDbContext>(schema, "GeneralPolicyEndorsed");
        services.AddIntegrationEventHandler<GeneralPolicyCancelled, GeneralPolicyBilling, BillingDbContext>(schema, "GeneralPolicyCancelled");
        services.AddIntegrationEventHandler<LifePolicyIssued, LifePolicyBilling, BillingDbContext>(schema, "LifePolicyIssued");
        services.AddIntegrationEventHandler<LifePolicyLapsed, LifePolicyBilling, BillingDbContext>(schema, "LifePolicyLapsed");
        services.AddIntegrationEventHandler<LifePolicyReinstated, LifePolicyBilling, BillingDbContext>(schema, "LifePolicyReinstated");
        services.AddIntegrationEventHandler<Health.Contracts.HealthMemberEnrolled, HealthContributionBilling, BillingDbContext>(schema, "HealthMemberEnrolled");
        services.AddIntegrationEventHandler<Health.Contracts.DependantAdded, HealthContributionBilling, BillingDbContext>(schema, "DependantAdded");
        services.AddIntegrationEventHandler<Health.Contracts.HealthMemberStatusChanged, HealthContributionBilling, BillingDbContext>(schema, "MemberStatusChanged");
        services.AddIntegrationEventHandler<Health.Contracts.MembershipProductAssigned, HealthContributionBilling, BillingDbContext>(schema, "MembershipProductAssigned");
        services.AddOptions<BillingJobOptions>().BindConfiguration("Billing:Jobs");
        services.AddSingleton<BillingJobs>();
        if (!InfrastructureRegistration.IsBuildTimeDocumentGeneration)
        {
            services.AddHostedService<BillingJobsService>();
        }

        services.AddScoped<IBillingPortalQueries, BillingPortalQueries>();
        services.AddSingleton<BuildingBlocks.Application.Context.IPortalRecordRelations, BillingPortalRelations>(); // external-user access §2
        // Onboarding readiness (addenda/onboarding-readiness.md §2): the configuration this module owns.
        BuildingBlocks.Infrastructure.Readiness.ReadinessRegistration.AddStaticReadiness(services, Persistence.BillingDbContext.SchemaName, BillingStaticData.Descriptors,
            new BuildingBlocks.Infrastructure.Readiness.StaticCheck("PAYMENTS_AND_DISTRIBUTION", "COLLECTION_METHODS", "billing.ref_collection_methods", "Collection methods, billing frequencies, document numbering and ageing buckets covering every day to the last action"),
            new BuildingBlocks.Infrastructure.Readiness.StaticCheck("PAYMENTS_AND_DISTRIBUTION", "COLLECTION_METHODS", "billing.ref_billing_frequencies", "Collection methods, billing frequencies, document numbering and ageing buckets covering every day to the last action", null, GlobalRows: true),
            new BuildingBlocks.Infrastructure.Readiness.StaticCheck("PAYMENTS_AND_DISTRIBUTION", "COLLECTION_METHODS", "billing.cfg_document_numbering_schemes", "Collection methods, billing frequencies, document numbering and ageing buckets covering every day to the last action"));
        BuildingBlocks.Infrastructure.Readiness.ReadinessRegistration.AddSqlReadiness(services, Persistence.BillingDbContext.SchemaName, ["PAYMENTS_AND_DISTRIBUTION"], async (context, connection, transaction, ct) =>
        {
            // Request 46: the buckets cover every day overdue from day 1 to the last action, with no gaps or overlaps.
            var buckets = await BuildingBlocks.Infrastructure.Readiness.ReadinessSql.Strings(connection, transaction,
                $"SELECT days_from || ':' || days_to || ':' || action FROM billing.cfg_arrears_ageing_buckets WHERE tenant_id = @t AND {BuildingBlocks.Infrastructure.Readiness.ReadinessSql.InForce} ORDER BY days_from, days_to",
                ct, ("t", context.TenantId), ("inForce", BuildingBlocks.Infrastructure.StaticData.StaticLifecycle.Configuration.InForce), ("day", context.Day));
            return [BuildingBlocks.Application.ReadinessResult.Of("PAYMENTS_AND_DISTRIBUTION", "COLLECTION_METHODS",
                "Collection methods, billing frequencies, document numbering and ageing buckets covering every day to the last action", AgeingCoverage.Gaps(buckets))];
        });
        return services;
    }
}
