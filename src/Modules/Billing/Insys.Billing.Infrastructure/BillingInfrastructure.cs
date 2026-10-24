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
        services.AddOptions<BillingJobOptions>().BindConfiguration("Billing:Jobs");
        services.AddSingleton<BillingJobs>();
        if (!InfrastructureRegistration.IsBuildTimeDocumentGeneration)
        {
            services.AddHostedService<BillingJobsService>();
        }

        return services;
    }
}
