/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : ClaimsFramework
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
        // Its approval kinds and the screens that open their records (BR-005).
        BuildingBlocks.Contracts.Approvals.ApprovalKinds.Declare(Insys.ClaimsFramework.Contracts.ClaimsApprovals.InstructionBusinessRefType, "CLAIM_PAYMENT_INSTRUCTIONS", Insys.ClaimsFramework.Contracts.ClaimsApprovals.InstructionApprovalType);
        const string schema = ClaimsFrameworkDbContext.SchemaName;
        services.AddModuleDbContext<ClaimsFrameworkDbContext>(schema, "ClaimsFramework");
        services.AddStaticData<ClaimsFrameworkDbContext>(schema, ClaimsFrameworkStaticData.Descriptors);
        services.AddModuleHandlers<ClaimsFrameworkDbContext>(typeof(AllocateClaimNumber).Assembly);
        services.AddDomainEventMapper<ClaimsEventMapper>(schema);
        services.AddScoped<IClaimsRepository, ClaimsRepository>();
        services.AddSingleton<Workflow.Contracts.IApprovalScopeSource, ClaimsApprovalScopes>();
        services.AddSingleton<BuildingBlocks.Contracts.IBusinessRecordScopes, ClaimsRecordScopes>();
        services.AddScoped<IClaimsCodes, ClaimsCodes>();
        services.AddIntegrationEventHandler<ApprovalGranted, ClaimsPolicies, ClaimsFrameworkDbContext>(schema, "ApprovalGranted");
        services.AddIntegrationEventHandler<Communications.Contracts.NotificationSent, LogClaimNotice, ClaimsFrameworkDbContext>(schema);
        services.AddIntegrationEventHandler<ApprovalRejected, ClaimsPolicies, ClaimsFrameworkDbContext>(schema, "ApprovalRejected");
        services.AddIntegrationEventHandler<PaymentSucceeded, ClaimsPolicies, ClaimsFrameworkDbContext>(schema, "PaymentSucceeded");
        services.AddOptions<ClaimsPaymentOptions>().BindConfiguration("Claims:Payments");
        services.AddIntegrationEventHandler<PaymentInitiated, ClaimsPolicies, ClaimsFrameworkDbContext>(schema, "PaymentInitiated");
        services.AddIntegrationEventHandler<PaymentFailed, ClaimsPolicies, ClaimsFrameworkDbContext>(schema, "PaymentFailed");
        services.AddIntegrationEventHandler<ClaimPaymentCompleted, ClaimsPolicies, ClaimsFrameworkDbContext>(schema, "ClaimPaymentCompleted");
        services.AddIntegrationEventHandler<GeneralClaimApproved, ClaimsPolicies, ClaimsFrameworkDbContext>(schema, "GeneralClaimApproved");
        services.AddIntegrationEventHandler<Life.Contracts.LifeClaimApproved, ClaimsPolicies, ClaimsFrameworkDbContext>(schema, "LifeClaimApproved");
        services.AddIntegrationEventHandler<RecoveryRecorded, ClaimsPolicies, ClaimsFrameworkDbContext>(schema, "RecoveryRecorded");
        services.AddScoped<IClaimRegistry, ClaimRegistry>();
        services.AddSingleton<BuildingBlocks.Infrastructure.StaticData.IStaticRecordValidator, Persistence.FraudIndicatorValidator>();
        // Onboarding readiness (addenda/onboarding-readiness.md §2): the configuration this module owns.
        BuildingBlocks.Infrastructure.Readiness.ReadinessRegistration.AddStaticReadiness(services, Persistence.ClaimsFrameworkDbContext.SchemaName, ClaimsFrameworkStaticData.Descriptors,
            new BuildingBlocks.Infrastructure.Readiness.StaticCheck("DOMAIN_REFERENCE_DATA", "CLAIMS_SETUP", "claims.cfg_claim_numbering_schemes", "Claim numbering schemes and reserve types"),
            new BuildingBlocks.Infrastructure.Readiness.StaticCheck("DOMAIN_REFERENCE_DATA", "CLAIMS_SETUP", "claims.ref_reserve_types", "Claim numbering schemes and reserve types", null, GlobalRows: true));
        return services;
    }
}
