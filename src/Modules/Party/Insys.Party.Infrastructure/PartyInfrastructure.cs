/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Party
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
using Insys.Party.Application;
using Insys.Party.Contracts;
using Insys.Party.Infrastructure.Persistence;
using Insys.Party.Infrastructure.StaticData;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Party.Infrastructure;

public static class PartyInfrastructure
{
    public static IServiceCollection AddPartyInfrastructure(this IServiceCollection services)
    {
        // Its approval kinds and the screens that open their records (BR-005).
        BuildingBlocks.Contracts.Approvals.ApprovalKinds.Declare(Insys.Party.Contracts.PartyApprovals.BankAccountBusinessRefType, "PARTY_BANK_ACCOUNTS", Insys.Party.Contracts.PartyApprovals.BankAccountVerificationType);
        BuildingBlocks.Contracts.Approvals.ApprovalKinds.Declare(Insys.Party.Contracts.PartyApprovals.MatchBusinessRefType, "PARTIES", Insys.Party.Contracts.PartyApprovals.MatchResolutionType);
        BuildingBlocks.Contracts.Approvals.ApprovalKinds.Declare(Insys.Party.Contracts.PartyApprovals.SearchLogHoldBusinessRefType, "PARTY_SEARCH_LOG", Insys.Party.Contracts.PartyApprovals.LegalHoldReleaseType);
        services.AddModuleDbContext<PartyDbContext>(PartyDbContext.SchemaName, "Party");
        // D-34: a legal hold on a party itself; a party has no product or node.
        services.AddLegalHoldTargets(PartyDbContext.SchemaName, new Dictionary<string, string>
        {
            ["PARTY"] = "SELECT NULL::text, NULL::text FROM party.party WHERE id = @id",
        });
        services.AddStaticData<PartyDbContext>(PartyDbContext.SchemaName, PartyStaticData.Descriptors);
        services.AddModuleHandlers<PartyDbContext>(typeof(RegisterParty).Assembly);
        services.AddDomainEventMapper<PartyEventMapper>(PartyDbContext.SchemaName);
        services.AddScoped<IPartyRepository, PartyRepository>();
        services.AddScoped<AnonymisationCheck>(); // D-34
        services.AddScoped<IRecordRetentionPolicies, RecordRetentionPolicyReader>(); // OQ-120
        services.AddScoped<IPartySearch, PartySearchStore>();
        services.AddScoped<ISearchLogRetention, SearchLogRetentionStore>();
        services.AddOptions<SearchLogRetentionOptions>().BindConfiguration("Party:SearchLogRetention");
        services.AddSingleton<SearchLogRetentionJob>();
        if (!BuildingBlocks.Infrastructure.InfrastructureRegistration.IsBuildTimeDocumentGeneration)
        {
            services.AddHostedService<SearchLogRetentionService>();
        }
        services.AddScoped<IPartyCodes, PartyCodes>();
        services.AddScoped<IPartyPortalQueries, PartyPortalQueries>(); // BR-091
        services.AddSingleton<BankAccountQueries>();
        services.AddSingleton<IBankAccounts>(sp => sp.GetRequiredService<BankAccountQueries>());
        services.AddSingleton<IBankAccountDetails>(sp => sp.GetRequiredService<BankAccountQueries>());
        services.AddSingleton<IPersonFacts>(sp => sp.GetRequiredService<BankAccountQueries>());
        services.AddSingleton<IPartyDirectory>(sp => sp.GetRequiredService<BankAccountQueries>());
        services.AddSingleton<IPartyLanguage>(sp => sp.GetRequiredService<BankAccountQueries>());
        services.AddSingleton<IPartyContacts>(sp => sp.GetRequiredService<BankAccountQueries>());
        services.AddSingleton<IPartyConsent>(sp => sp.GetRequiredService<BankAccountQueries>());
        services.AddIntegrationEventHandler<Communications.Contracts.MarketingOptedOut, WithdrawConsentOnOptOut, PartyDbContext>(PartyDbContext.SchemaName);
        services.AddIntegrationEventHandler<Contracts.PartyIdentifierAdded, IdentityVerificationPolicy, PartyDbContext>(PartyDbContext.SchemaName); // F001
        services.AddIntegrationEventHandler<Workflow.Contracts.ApprovalGranted, BankAccountVerificationPolicy, PartyDbContext>(PartyDbContext.SchemaName, "ApprovalGranted");
        services.AddIntegrationEventHandler<Workflow.Contracts.ApprovalGranted, PartyMatchResolutionPolicy, PartyDbContext>(PartyDbContext.SchemaName, "ApprovalGranted");
        services.AddIntegrationEventHandler<Workflow.Contracts.ApprovalRejected, PartyMatchResolutionPolicy, PartyDbContext>(PartyDbContext.SchemaName, "ApprovalRejected");
        services.AddIntegrationEventHandler<Workflow.Contracts.ApprovalRejected, BankAccountVerificationPolicy, PartyDbContext>(PartyDbContext.SchemaName, "ApprovalRejected");
        services.AddIntegrationEventHandler<Workflow.Contracts.ApprovalGranted, SearchLogHoldReleasePolicy, PartyDbContext>(PartyDbContext.SchemaName, "ApprovalGranted");
        services.AddIntegrationEventHandler<Workflow.Contracts.ApprovalRejected, SearchLogHoldReleasePolicy, PartyDbContext>(PartyDbContext.SchemaName, "ApprovalRejected");
        return services;
    }
}
