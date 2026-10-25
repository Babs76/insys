/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Distribution
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
using Insys.BuildingBlocks.Contracts.StaticData;
using Insys.BuildingBlocks.Infrastructure;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Distribution.Application;
using Insys.Distribution.Contracts;
using Insys.Distribution.Infrastructure.Persistence;
using Insys.Distribution.Infrastructure.StaticData;
using Insys.General.Contracts;
using Insys.Life.Contracts;
using Insys.Payments.Contracts;
using Insys.Workflow.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Distribution.Infrastructure;

public static class DistributionInfrastructure
{
    public static IServiceCollection AddDistributionInfrastructure(this IServiceCollection services)
    {
        // Its approval kinds and the screens that open their records (BR-005).
        BuildingBlocks.Contracts.Approvals.ApprovalKinds.Declare(Insys.Distribution.Contracts.DistributionApprovals.StatementBusinessRefType, "COMMISSION_STATEMENTS", Insys.Distribution.Contracts.DistributionApprovals.StatementApprovalType);
        // ADR-026 §2: this module's event consumers run under its JOB function.
        BuildingBlocks.Application.Context.JobFunctions.ForConsumers(Persistence.DistributionDbContext.SchemaName, "DISTRIBUTION_EVENT_PROCESSING");
        const string schema = DistributionDbContext.SchemaName;
        services.AddModuleDbContext<DistributionDbContext>(schema, "Distribution");
        services.AddStaticData<DistributionDbContext>(schema, DistributionStaticData.Descriptors);
        services.AddModuleHandlers<DistributionDbContext>(typeof(PrepareStatement).Assembly);
        services.AddDomainEventMapper<DistributionEventMapper>(schema);
        services.AddScoped<IDistributionRepository, DistributionRepository>();
        services.AddScoped<IDistributionPortalQueries, DistributionPortalQueries>();
        services.AddScoped<IDistributionCodes, DistributionCodes>();
        services.AddSingleton<IIntermediaries, Intermediaries>();
        services.AddSingleton<IStaticChangeGuard, IntermediaryLifecycleGuard>();
        services.AddIntegrationEventHandler<InvoiceIssued, CommissionPolicies, DistributionDbContext>(schema, "InvoiceIssued");
        services.AddIntegrationEventHandler<InvoiceSettled, CommissionPolicies, DistributionDbContext>(schema, "InvoiceSettled");
        services.AddIntegrationEventHandler<GeneralPolicyCancelled, CommissionPolicies, DistributionDbContext>(schema, "GeneralPolicyCancelled");
        services.AddIntegrationEventHandler<LifePolicyLapsed, CommissionPolicies, DistributionDbContext>(schema, "LifePolicyLapsed");
        services.AddIntegrationEventHandler<ApprovalGranted, CommissionPolicies, DistributionDbContext>(schema, "ApprovalGranted");
        services.AddIntegrationEventHandler<ApprovalRejected, CommissionPolicies, DistributionDbContext>(schema, "ApprovalRejected");
        services.AddIntegrationEventHandler<PaymentSucceeded, CommissionPolicies, DistributionDbContext>(schema, "PaymentSucceeded");
        services.AddIntegrationEventHandler<StaticDataChangeApproved, IntermediaryStatusPolicy, DistributionDbContext>(schema, "IntermediaryStatus");
        services.AddSingleton<BuildingBlocks.Application.Context.IPortalRecordRelations, DistributionPortalRelations>(); // external-user access §2
        return services;
    }
}
